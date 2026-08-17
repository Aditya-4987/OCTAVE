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
    private readonly HashSet<int> _fadingStreams = new();

    private long _sessionIdCounter = 0;
    private long _currentSessionId = 0;
    private string _currentSourceUri = string.Empty;

    public event EventHandler<string>? TrackStarted;
    public event EventHandler<TrackEndedEventArgs>? TrackEnded;
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
    private float _preampGainDb = 0.0f;

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

    private int _crossfadeDurationMs = 1000; // Default 1000ms (1 second) smooth crossfade
    public int CrossfadeDurationMs
    {
        get => _crossfadeDurationMs;
        set => _crossfadeDurationMs = Math.Max(0, value);
    }

    public long Play(string urlOrPath, double replayGain = 0.0)
    {
        if (!_isInitialized) Init();

        long sessionId = Interlocked.Increment(ref _sessionIdCounter);
        bool started = false;
        lock (_streamLock)
        {
            int oldStream = _currentStream;
            int fadeMs = _crossfadeDurationMs;
            bool isOldPlaying = oldStream != 0 && Bass.ChannelIsActive(oldStream) == ManagedBass.PlaybackState.Playing;

            if (oldStream != 0)
            {
                RemoveEqUnlocked(); // Detach EQ handles from old stream so new stream can claim FX

                if (fadeMs > 0 && isOldPlaying)
                {
                    FadeAndFreeStream(oldStream, fadeMs);
                }
                else
                {
                    Bass.ChannelStop(oldStream);
                    Bass.StreamFree(oldStream);
                }
                _currentStream = 0;
            }

            _currentSessionId = sessionId;
            _currentSourceUri = urlOrPath;

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
                float targetGain = (float)Math.Pow(10, replayGain / 20.0);
                _currentReplayGainScale = Math.Clamp(targetGain, 0.1f, 2.0f);

                float preampScale = (float)Math.Pow(10, _preampGainDb / 20.0);
                float finalTargetVolume = Math.Clamp((_isMuted ? 0f : _volume) * _currentReplayGainScale * preampScale, 0f, 2.0f);

                if (fadeMs > 0 && isOldPlaying)
                {
                    // Start new stream at 0 volume and slide up smoothly to finalTargetVolume
                    Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, 0f);
                    Bass.ChannelPlay(stream);
                    Bass.ChannelSlideAttribute(stream, ChannelAttribute.Volume, finalTargetVolume, fadeMs);
                }
                else
                {
                    UpdateStreamVolumeUnlocked();
                    Bass.ChannelPlay(stream);
                }

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

                started = true;
                Debug.WriteLine($"[OCTAVE ENGINE] Playing stream ID: {stream}, Session ID: {sessionId} (Crossfade: {fadeMs}ms)");
            }
            else
            {
                Debug.WriteLine($"[OCTAVE ENGINE] Stream creation failed! Path: {urlOrPath}, BASS Error: {Bass.LastError}");
                _currentStream = 0;
            }
        }

        if (started)
        {
            // Start periodic position reporting and notify listeners outside the lock
            StartPositionTimer();
            TrackStarted?.Invoke(this, urlOrPath);
        }
        else
        {
            // On stream load failure (corrupted file, unsupported format), auto-advance queue off-thread with SessionId
            var handler = TrackEnded;
            if (handler != null)
            {
                long endedSession = sessionId;
                string endedUri = urlOrPath;
                ThreadPool.QueueUserWorkItem(_ => handler.Invoke(this, new TrackEndedEventArgs(endedSession, endedUri)));
            }
        }

        return sessionId;
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

    private void FadeAndFreeStream(int streamToFree, int fadeMs)
    {
        if (streamToFree == 0) return;

        lock (_streamLock)
        {
            _fadingStreams.Add(streamToFree);
        }

        Bass.ChannelSlideAttribute(streamToFree, ChannelAttribute.Volume, 0f, fadeMs);
        Task.Run(async () =>
        {
            await Task.Delay(fadeMs + 100);
            lock (_streamLock)
            {
                if (_fadingStreams.Remove(streamToFree))
                {
                    Bass.ChannelStop(streamToFree);
                    Bass.StreamFree(streamToFree);
                }
            }
        });
    }

    public void Stop()
    {
        StopPositionTimer();
        lock (_streamLock)
        {
            if (_currentStream != 0)
            {
                int streamToStop = _currentStream;
                _currentStream = 0;
                RemoveEqUnlocked();

                if (_crossfadeDurationMs > 0 && Bass.ChannelIsActive(streamToStop) == ManagedBass.PlaybackState.Playing)
                {
                    int fadeMs = Math.Min(_crossfadeDurationMs, 500); // 500ms quick fade out on explicit stop
                    FadeAndFreeStream(streamToStop, fadeMs);
                }
                else
                {
                    Bass.ChannelStop(streamToStop);
                    Bass.StreamFree(streamToStop);
                }
            }
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

    public float PreampGainDb => _preampGainDb;

    public void SetPreampGain(float gainDb)
    {
        _preampGainDb = Math.Clamp(gainDb, -15f, 15f);
        lock (_streamLock)
        {
            UpdateStreamVolumeUnlocked();
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
            float preampScale = (float)Math.Pow(10, _preampGainDb / 20.0);
            float effectiveVolume = _isMuted ? 0f : Math.Clamp(_volume * _currentReplayGainScale * preampScale, 0f, 2f);
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

    public string OutputDeviceName
    {
        get
        {
            var (name, _, _, _) = Octave.Core.Helpers.WindowsAudioDeviceHelper.GetDefaultOutputDeviceDetails();
            if (!string.IsNullOrWhiteSpace(name) && name != "Default Audio Device") return name;

            if (_isInitialized)
            {
                int currentDev = Bass.CurrentDevice;
                if (currentDev >= 0 && Bass.GetDeviceInfo(currentDev, out var info))
                {
                    return string.IsNullOrWhiteSpace(info.Name) ? "Default Audio Device" : info.Name;
                }
            }
            return "Default Audio Device";
        }
    }

    public string OutputDeviceQuality
    {
        get
        {
            var (_, format, _, _) = Octave.Core.Helpers.WindowsAudioDeviceHelper.GetDefaultOutputDeviceDetails();
            if (format != "Unknown") return format;

            if (_isInitialized && Bass.GetInfo(out var info))
            {
                double khz = info.SampleRate / 1000.0;
                return $"{khz:0.0}kHz (Shared Mode)";
            }
            return "Unknown";
        }
    }

    public AudioQualityDetails QualityDetails
    {
        get
        {
            var (devName, devFormat, devKhz, devBits) = Octave.Core.Helpers.WindowsAudioDeviceHelper.GetDefaultOutputDeviceDetails();
            if (string.IsNullOrWhiteSpace(devName)) devName = "Default Audio Device";
            if (string.IsNullOrWhiteSpace(devFormat) || devFormat == "Unknown")
            {
                if (_isInitialized && Bass.GetInfo(out var bInfo))
                {
                    devKhz = bInfo.SampleRate / 1000.0;
                    devFormat = $"{devKhz:0.0}kHz (Shared Mode)";
                }
                else
                {
                    devFormat = "Unknown Output Format";
                }
            }

            int bits = 16;
            double sourceKhz = 44.1;
            int channels = 2;
            string codecFormat = "Audio Stream";
            string decoderEngine = "BASS Core Audio Engine";

            lock (_streamLock)
            {
                if (_currentStream != 0 && Bass.ChannelGetInfo(_currentStream, out var info))
                {
                    bits = (info.OriginalResolution > 0) ? info.OriginalResolution : 16;
                    sourceKhz = info.Frequency / 1000.0;
                    channels = info.Channels;

                    decoderEngine = info.ChannelType switch
                    {
                        ChannelType.FLAC => "BASS_FLAC Native Decoder",
                        ChannelType.AAC => "BASS_AAC Native Decoder",
                        ChannelType.MP4 => "BASS_AAC MP4 Decoder",
                        ChannelType.OGG => "BASS Ogg Vorbis Decoder",
                        ChannelType.MP3 => "BASS MP3 MPEG Decoder",
                        ChannelType.WMA => "BASS_WMA Media Decoder",
                        ChannelType.DSD => "BASS_DSD DSD Decoder",
                        ChannelType.APE => "BASS_APE Monkey's Audio Decoder",
                        ChannelType.Wave or ChannelType.WavePCM => "BASS PCM Wave Decoder",
                        _ => "BASS Core Audio Engine"
                    };

                    codecFormat = info.ChannelType switch
                    {
                        ChannelType.FLAC => "FLAC Lossless Audio",
                        ChannelType.AAC => "AAC Lossy Compressed",
                        ChannelType.MP4 => "M4A AAC Audio",
                        ChannelType.OGG => "Ogg Vorbis Audio",
                        ChannelType.MP3 => "MP3 MPEG Audio",
                        ChannelType.WMA => "WMA Windows Media",
                        ChannelType.DSD => "DSD Direct Stream Digital",
                        ChannelType.APE => "APE Lossless Audio",
                        ChannelType.Wave or ChannelType.WavePCM => "WAV Uncompressed PCM",
                        _ => "Audio Stream"
                    };
                }
            }

            string channelsText = channels switch
            {
                1 => "1 Ch (Mono)",
                2 => "2 Ch (Stereo)",
                6 => "6 Ch (5.1 Surround)",
                8 => "8 Ch (7.1 Surround)",
                _ => $"{channels} Channels"
            };

            string resamplingStatus;
            if (Math.Abs(sourceKhz - devKhz) < 0.1)
            {
                resamplingStatus = $"Bit-Matched Target ({sourceKhz:0.0}kHz)";
            }
            else if (sourceKhz > devKhz)
            {
                resamplingStatus = $"OS Downsampled ({sourceKhz:0.0}kHz → {devKhz:0.0}kHz)";
            }
            else
            {
                resamplingStatus = $"OS Upsampled ({sourceKhz:0.0}kHz → {devKhz:0.0}kHz)";
            }

            string qualityBadgeType = "Standard";
            if (bits >= 24 || sourceKhz >= 88.2)
            {
                qualityBadgeType = "HiRes";
            }
            else if (bits == 16 && sourceKhz >= 44.1 && (codecFormat.Contains("Lossless") || codecFormat.Contains("PCM") || codecFormat.Contains("FLAC") || codecFormat.Contains("DSD")))
            {
                qualityBadgeType = "CDQuality";
            }

            string gainText = Math.Abs(_currentReplayGainScale - 1.0f) > 0.01f
                ? $"ReplayGain ({(20.0 * Math.Log10(_currentReplayGainScale)):+0.0;-0.0;0.0}dB)"
                : "Standard Level";
            string preampText = Math.Abs(_preampGainDb) > 0.01f ? $" | Preamp: {_preampGainDb:+0.0;-0.0;0.0}dB" : "";
            string dspStatus = _eqEnabled ? $"10-Band EQ Active ({gainText}{preampText})" : $"Direct Passthrough ({gainText}{preampText})";

            string streamQualityStr = $"{bits}-bit {sourceKhz:0.0}kHz";

            return new AudioQualityDetails(
                StreamQuality: streamQualityStr,
                CodecFormat: codecFormat,
                BitDepth: bits,
                SampleRateKhz: sourceKhz,
                ChannelsText: channelsText,
                DecoderEngine: decoderEngine,
                ResamplingStatus: resamplingStatus,
                QualityBadgeType: qualityBadgeType,
                OutputDeviceName: devName,
                OutputDeviceQuality: devFormat,
                DspStatus: dspStatus
            );
        }
    }

    private readonly float[] _rawFftBuffer = new float[256];
    private float[]? _lastFftPeaks;

    public float[] GetFftData(int binCount = 36)
    {
        if (binCount <= 0) binCount = 36;
        var result = new float[binCount];
        if (_lastFftPeaks == null || _lastFftPeaks.Length != binCount)
        {
            _lastFftPeaks = new float[binCount];
        }

        lock (_streamLock)
        {
            if (_currentStream == 0 || Bass.ChannelIsActive(_currentStream) != ManagedBass.PlaybackState.Playing)
            {
                for (int i = 0; i < binCount; i++)
                {
                    _lastFftPeaks[i] = Math.Max(0f, _lastFftPeaks[i] * 0.85f);
                    result[i] = _lastFftPeaks[i];
                }
                return result;
            }

            int read = Bass.ChannelGetData(_currentStream, _rawFftBuffer, (int)DataFlags.FFT512);
            if (read <= 0)
            {
                for (int i = 0; i < binCount; i++)
                {
                    _lastFftPeaks[i] = Math.Max(0f, _lastFftPeaks[i] * 0.85f);
                    result[i] = _lastFftPeaks[i];
                }
                return result;
            }

            for (int i = 0; i < binCount; i++)
            {
                int startBin = (int)Math.Pow(256.0, (double)i / binCount);
                int endBin = (int)Math.Pow(256.0, (double)(i + 1) / binCount);
                startBin = Math.Clamp(startBin, 0, 255);
                endBin = Math.Clamp(endBin, startBin + 1, 256);

                float maxVal = 0f;
                for (int b = startBin; b < endBin; b++)
                {
                    if (_rawFftBuffer[b] > maxVal) maxVal = _rawFftBuffer[b];
                }

                float target = Math.Clamp((float)(Math.Sqrt(maxVal) * 1.8), 0.02f, 1.0f);
                if (target > _lastFftPeaks[i])
                {
                    _lastFftPeaks[i] = _lastFftPeaks[i] * 0.3f + target * 0.7f;
                }
                else
                {
                    _lastFftPeaks[i] = _lastFftPeaks[i] * 0.82f + target * 0.18f;
                }

                result[i] = _lastFftPeaks[i];
            }
        }

        return result;
    }

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
            long endedSession;
            string endedUri;
            lock (_streamLock)
            {
                endedSession = _currentSessionId;
                endedUri = _currentSourceUri;
            }
            ThreadPool.QueueUserWorkItem(_ => handler.Invoke(this, new TrackEndedEventArgs(endedSession, endedUri)));
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
                foreach (var fading in _fadingStreams)
                {
                    Bass.ChannelStop(fading);
                    Bass.StreamFree(fading);
                }
                _fadingStreams.Clear();

                // Free BASS stream if open
                if (_currentStream != 0)
                {
                    RemoveEqUnlocked();
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
