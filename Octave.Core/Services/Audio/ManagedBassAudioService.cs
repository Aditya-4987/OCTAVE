using ManagedBass;
using ManagedBass.Fx;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Octave.Core.Models;

namespace Octave.Core.Services.Audio;

public class ManagedBassAudioService : IAudioPlayerService, IDisposable
{
    private int _currentStream = 0;
    private bool _isInitialized = false;
    private bool _disposed = false;

    private float _volume = 0.5f; // Master volume backup
    private bool _isMuted = false;

    // 10-band graphic EQ (ISO center frequencies). Gains persist across tracks;
    // the FX is re-attached to each new stream. Requires bass_fx.dll at runtime.
    private static readonly int[] EqFreqs = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    private readonly float[] _eqGains = new float[10];
    private bool _eqEnabled;
    private int _eqFxHandle;

    private readonly SyncProcedure _endSyncCallback;
    private Timer? _positionTimer;
    private readonly object _timerLock = new();

    // Guards every access to the unmanaged stream handle. Play/Stop run on the
    // UI thread, the position timer runs on a thread-pool thread, and the End
    // sync fires on BASS's own unmanaged thread - without this lock those races
    // can call into a freed handle.
    private readonly object _streamLock = new();

    public event EventHandler<string>? TrackStarted;
    public event EventHandler? TrackEnded;
    public event EventHandler<double>? PositionChanged;

    public ManagedBassAudioService()
    {
        _endSyncCallback = OnTrackEndedCallback;
    }

    // Decoder add-ons that extend the core bass.dll (which only handles
    // MP3/MP2/MP1/OGG/WAV/AIFF). Drop the matching un4seen binaries next to
    // bass.dll and these formats start playing - missing ones are skipped.
    private static readonly string[] PluginFileNames =
    {
        "bass_fx.dll",   // EQ & DSP
        "bassflac.dll",  // FLAC
        "bassopus.dll",  // Opus / .opus
        "bass_aac.dll",  // AAC / M4A / MP4
        "bassalac.dll",  // Apple Lossless (ALAC)
        "basswma.dll",   // WMA
        "bassdsd.dll",   // DSD (.dsf / .dff)
        "bass_ape.dll"   // Monkey's Audio (APE)
    };

    private float _currentReplayGainScale = 1.0f;

    public bool Init()
    {
        if (_isInitialized) return true;

        // Tells BASS to dynamically follow the default Windows output device (e.g. bluetooth disconnect).
        // Must be configured BEFORE calling Bass.Init.
        Bass.Configure(Configuration.IncludeDefaultDevice, true);
        Bass.Configure(Configuration.DevNonStop, true);

        // Attempt 1: Init with Default Windows Audio Device (-1), 44.1kHz
        _isInitialized = Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero);

        if (!_isInitialized)
        {
            Debug.WriteLine($"[OCTAVE ENGINE] BASS Init (-1) Failed (Error: {Bass.LastError}). Falling back to No Sound device (0)...");
            // Attempt 2: Fallback to No Sound device (0) so engine calls remain safe
            _isInitialized = Bass.Init(0, 44100, DeviceInitFlags.Default, IntPtr.Zero);
        }

        if (!_isInitialized)
        {
            Debug.WriteLine($"[OCTAVE ENGINE] BASS Init Failed. Error: {Bass.LastError}");
            return false;
        }

        LoadPlugins();
        
        try 
        {
            _ = ManagedBass.Fx.BassFx.Version; 
        } 
        catch (Exception ex) 
        {
            Debug.WriteLine($"[OCTAVE ENGINE] Failed to initialize BASS_FX: {ex.Message}");
        }

        return _isInitialized;
    }

    private void LoadPlugins()
    {
        string baseDir = AppContext.BaseDirectory;
        foreach (var name in PluginFileNames)
        {
            try
            {
                // Try the app directory first, then let BASS resolve by name.
                string fullPath = System.IO.Path.Combine(baseDir, name);
                string target = System.IO.File.Exists(fullPath) ? fullPath : name;

                int handle = Bass.PluginLoad(target);
                if (handle == 0)
                {
                    Debug.WriteLine($"[OCTAVE ENGINE] Decoder plugin not loaded: {name} (Error: {Bass.LastError})");
                }
                else
                {
                    Debug.WriteLine($"[OCTAVE ENGINE] Decoder plugin loaded: {name}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCTAVE ENGINE] Plugin load threw for {name}: {ex.Message}");
            }
        }
    }

    public void Play(string urlOrPath, double replayGain = 0.0)
    {
        if (!_isInitialized) Init();

        bool started = false;
        lock (_streamLock)
        {
            FreeStreamInternal(); // Kill any currently playing track and remove FX handles

            int stream;
            // Check if we were handed an HTTP web stream or a local hard drive path
            if (urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                stream = Bass.CreateStream(urlOrPath, 0, BassFlags.Default, null, IntPtr.Zero);
            }
            else
            {
                stream = Bass.CreateStream(urlOrPath, 0, 0, BassFlags.Default);
            }

            if (stream != 0)
            {
                _currentStream = stream;

                // Register end sync procedure
                Bass.ChannelSetSync(stream, SyncFlags.End | SyncFlags.Mixtime, 0, _endSyncCallback, IntPtr.Zero);

                // Convert ReplayGain (dB) to a linear scalar (10^(dB/20)).
                // If ReplayGain is 0.0, this naturally results in 1.0f.
                float targetGain = (float)Math.Pow(10, replayGain / 20.0);
                
                // Cap the ReplayGain amplifier to prevent clipping. 
                // A maximum multiplier of 2.0 corresponds to roughly +6dB.
                _currentReplayGainScale = Math.Clamp(targetGain, 0.1f, 2.0f);

                // Channel volume is set directly on the stream using process volume and ReplayGain scalar.
                UpdateStreamVolumeUnlocked();

                // Re-attach the EQ to the new stream if it's enabled.
                _eqFxHandle = 0;
                _limiterFxHandle = 0;
                if (_eqEnabled) SetupEqUnlocked();
                
                // Read streaming quality (e.g. 16-bit 44.1kHz)
                if (Bass.ChannelGetInfo(stream, out var info))
                {
                    int bits = (info.OriginalResolution > 0) ? info.OriginalResolution : 16;
                    double khz = info.Frequency / 1000.0;
                    StreamingQuality = $"{bits}-bit {khz:0.0}kHz";
                }
                else
                {
                    StreamingQuality = "Unknown";
                }

                Bass.ChannelPlay(stream);
                started = true;
                Debug.WriteLine($"[OCTAVE ENGINE] Playing stream ID: {stream}");
            }
            else
            {
                Debug.WriteLine($"[OCTAVE ENGINE] Stream creation failed! Path: {urlOrPath}, BASS Error: {Bass.LastError}");
                _currentStream = 0;
            }
        }

        if (started)
        {
            // Start periodic position reporting and notify listeners outside the
            // lock so handlers can never reenter the engine while it is held.
            StartPositionTimer();
            TrackStarted?.Invoke(this, urlOrPath);
        }
        else
        {
            // On stream load failure (corrupted file, unsupported format), auto-advance queue
            TrackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Pause()
    {
        StopPositionTimer();
        lock (_streamLock)
        {
            if (_currentStream != 0)
            {
                Bass.ChannelPause(_currentStream);
            }
        }
    }

    public void Resume()
    {
        bool resumed = false;
        lock (_streamLock)
        {
            if (_currentStream != 0)
            {
                Bass.ChannelPlay(_currentStream);
                resumed = true;
            }
        }
        if (resumed) StartPositionTimer();
    }

    public void Stop()
    {
        StopPositionTimer();
        lock (_streamLock)
        {
            FreeStreamInternal();
        }
    }

    // Frees the active stream. Caller MUST hold _streamLock.
    private void FreeStreamInternal()
    {
        if (_currentStream != 0)
        {
            RemoveEqUnlocked(); // Explicitly detach active FX handles before freeing stream
            Bass.ChannelStop(_currentStream);
            Bass.StreamFree(_currentStream);
            _currentStream = 0;
        }
    }

    // ---- Equalizer --------------------------------------------------------

    public IReadOnlyList<int> EqFrequencies => EqFreqs;

    public bool IsEqEnabled => _eqEnabled;

    public float[] GetEqGains() => (float[])_eqGains.Clone();

    public void SetEqEnabled(bool enabled)
    {
        lock (_streamLock)
        {
            _eqEnabled = enabled;
            if (_currentStream == 0) return;
            if (enabled) SetupEqUnlocked();
            else RemoveEqUnlocked();
        }
    }

    public void SetEqBand(int index, float gainDb)
    {
        if (index < 0 || index >= _eqGains.Length) return;
        gainDb = Math.Clamp(gainDb, -15f, 15f);
        lock (_streamLock)
        {
            _eqGains[index] = gainDb;
            if (_eqEnabled && _currentStream != 0 && _eqFxHandle != 0)
                ApplyBandUnlocked(index);
        }
    }

    private int _limiterFxHandle = 0;

    // Caller MUST hold _streamLock and have a live _currentStream.
    private void SetupEqUnlocked()
    {
        if (_currentStream == 0) return;
        try
        {
            if (_eqFxHandle == 0)
                _eqFxHandle = Bass.ChannelSetFX(_currentStream, EffectType.PeakEQ, 0);

            if (_limiterFxHandle == 0)
                _limiterFxHandle = Bass.ChannelSetFX(_currentStream, EffectType.Compressor, 1);

            if (_eqFxHandle == 0)
            {
                Debug.WriteLine($"[OCTAVE ENGINE] EQ unavailable (bass_fx.dll missing?). Error: {Bass.LastError}");
                return;
            }

            for (int i = 0; i < EqFreqs.Length; i++)
                ApplyBandUnlocked(i);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OCTAVE ENGINE] EQ setup failed: {ex.Message}");
        }
    }

    private void ApplyBandUnlocked(int index)
    {
        var p = new PeakEQParameters
        {
            fBandwidth = 2.5f,
            fCenter = EqFreqs[index],
            fGain = _eqGains[index],
            lBand = index
        };
        Bass.FXSetParameters(_eqFxHandle, p);
    }

    private void RemoveEqUnlocked()
    {
        if (_eqFxHandle != 0 && _currentStream != 0)
            Bass.ChannelRemoveFX(_currentStream, _eqFxHandle);
        _eqFxHandle = 0;
        
        if (_limiterFxHandle != 0 && _currentStream != 0)
            Bass.ChannelRemoveFX(_currentStream, _limiterFxHandle);
        _limiterFxHandle = 0;
    }

    public double GetDurationSeconds()
    {
        lock (_streamLock)
        {
            return _currentStream != 0
                ? Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetLength(_currentStream))
                : 0.0;
        }
    }

    public double GetPositionSeconds()
    {
        lock (_streamLock)
        {
            return _currentStream != 0
                ? Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetPosition(_currentStream))
                : 0.0;
        }
    }

    private void UpdateStreamVolumeUnlocked()
    {
        if (_currentStream != 0)
        {
            float effectiveVolume = _isMuted ? 0f : Math.Clamp(_volume * _currentReplayGainScale, 0f, 2f);
            Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, effectiveVolume);
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            _volume = clamped;
            if (_isMuted && clamped > 0f)
            {
                _isMuted = false;
            }
            lock (_streamLock)
            {
                UpdateStreamVolumeUnlocked();
            }
        }
    }

    public bool IsMuted => _isMuted;

    public void SetMuted(bool isMuted)
    {
        if (_isMuted == isMuted) return;
        _isMuted = isMuted;
        lock (_streamLock)
        {
            UpdateStreamVolumeUnlocked();
        }
    }

    public void ToggleMute()
    {
        SetMuted(!_isMuted);
    }

    public double PositionSeconds => GetPositionSeconds();

    public double DurationSeconds => GetDurationSeconds();

    public PlaybackStatus Status
    {
        get
        {
            lock (_streamLock)
            {
                if (_currentStream == 0) return PlaybackStatus.Stopped;
                var active = Bass.ChannelIsActive(_currentStream);
                return active switch
                {
                    ManagedBass.PlaybackState.Playing => PlaybackStatus.Playing,
                    ManagedBass.PlaybackState.Paused => PlaybackStatus.Paused,
                    ManagedBass.PlaybackState.Stalled => PlaybackStatus.Buffering,
                    _ => PlaybackStatus.Stopped
                };
            }
        }
    }

    public string StreamingQuality { get; private set; } = "Unknown";

    public void Seek(double positionSeconds)
    {
        lock (_streamLock)
        {
            if (_currentStream == 0) return;
            double duration = Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetLength(_currentStream));
            double clampedPosition = Math.Clamp(positionSeconds, 0, duration);
            long bytePosition = Bass.ChannelSeconds2Bytes(_currentStream, clampedPosition);
            Bass.ChannelSetPosition(_currentStream, bytePosition);
        }
    }

    private void OnTrackEndedCallback(int handle, int channel, int data, IntPtr user)
    {
        // This executes on BASS's unmanaged sync thread. Freeing the stream or
        // starting the next track from here (which the auto-advance does) is
        // unsafe and can deadlock the engine. Stop the timer and marshal the
        // TrackEnded notification onto a thread-pool thread so the callback can
        // return immediately and the queue advances on a clean managed thread.
        StopPositionTimer();
        var handler = TrackEnded;
        if (handler != null)
        {
            ThreadPool.QueueUserWorkItem(_ => handler.Invoke(this, EventArgs.Empty));
        }
    }

    private void StartPositionTimer()
    {
        lock (_timerLock)
        {
            _positionTimer?.Dispose();
            _positionTimer = new Timer(OnPositionTimerTick, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        }
    }

    private void StopPositionTimer()
    {
        lock (_timerLock)
        {
            _positionTimer?.Dispose();
            _positionTimer = null;
        }
    }

    private void OnPositionTimerTick(object? state)
    {
        double pos;
        lock (_streamLock)
        {
            if (_currentStream == 0 || Bass.ChannelIsActive(_currentStream) != ManagedBass.PlaybackState.Playing)
                return;
            pos = Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetPosition(_currentStream));
        }
        PositionChanged?.Invoke(this, pos);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                StopPositionTimer();
            }

            lock (_streamLock)
            {
                // Free BASS stream if open
                if (_currentStream != 0)
                {
                    Bass.ChannelStop(_currentStream);
                    Bass.StreamFree(_currentStream);
                    _currentStream = 0;
                }

                // Free BASS device context
                if (_isInitialized)
                {
                    Bass.Free();
                    _isInitialized = false;
                }
            }

            _disposed = true;
        }
    }

    ~ManagedBassAudioService()
    {
        Dispose(false);
    }
}
