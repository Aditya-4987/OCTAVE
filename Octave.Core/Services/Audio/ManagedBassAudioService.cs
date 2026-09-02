using ManagedBass;
using ManagedBass.Fx;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Octave.Core.Models;
using Octave.Core.Helpers;

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
    // LOAD-BEARING INVARIANT (see CODEBASE_AUDIT §12.4): no event
    // (PositionChanged/TrackStarted/TrackEnded) may be raised while this lock is
    // held - QueueService mutates under its own lock and then calls back into
    // this service; raising under _streamLock deadlocks the pair.
    private readonly object _streamLock = new();
    private readonly HashSet<int> _fadingStreams = new();

    // AUDIO-01: Play must not hold _streamLock across Bass.CreateStream (a
    // blocking network connect for HTTP sources). _playGate serializes
    // concurrent Play calls against each other instead; _streamLock is then
    // taken/released around each short handle mutation. Nothing acquires
    // _streamLock and then _playGate, so the ordering cannot deadlock.
    private readonly object _playGate = new();

    // Bumped on every Stop so an in-flight Play (whose stream is being created
    // outside the lock) can notice the user changed their mind and quietly
    // abandon the freshly-created stream instead of starting playback after
    // an explicit stop.
    private int _stopGeneration = 0;

    // AUDIO-07: per-stream End-sync identity. The sync callback used to read the
    // GLOBAL current session/uri, so a superseded/crossfaded stream's natural end
    // reported the incoming track's identity and advanced the queue past it.
    // Each stream's (sync handle, session id, uri) is registered here at
    // create-time and resolved from the callback's own `channel` argument.
    private readonly Dictionary<int, EndSyncIdentity> _endSyncs = new();

    private readonly struct EndSyncIdentity
    {
        public EndSyncIdentity(int syncHandle, long sessionId, string uri)
        {
            SyncHandle = syncHandle;
            SessionId = sessionId;
            Uri = uri;
        }

        public int SyncHandle { get; }
        public long SessionId { get; }
        public string Uri { get; }
    }

    private long _sessionIdCounter = 0;

    // AUDIO-02: true when no real output device could be initialized and BASS
    // fell back to the "No Sound" device - playback then runs silently. Play
    // retries the default device once per call until it succeeds.
    public bool IsSilentFallback { get; private set; }

    public event EventHandler<string>? TrackStarted;
    public event EventHandler<TrackEndedEventArgs>? TrackEnded;
    public event EventHandler<double>? PositionChanged;

    public ManagedBassAudioService()
    {
        _endSyncCallback = OnTrackEndedCallback;
        WindowsAudioDeviceHelper.AudioEndpointsChanged += OnAudioEndpointsChanged;
        WindowsAudioDeviceHelper.DefaultAudioEndpointChanged += OnDefaultAudioEndpointChanged;
    }

    // Decoder add-ons that extend the core bass.dll (which only handles
    // MP3/MP2/MP1/OGG/WAV/AIFF). Drop the matching un4seen binaries next to
    // bass.dll and these formats start playing - missing ones are skipped.
    // Note: bass_fx.dll is an FX library loaded directly by ManagedBass.Fx, not a stream plugin.
    private static readonly string[] PluginFileNames =
    {
        "bassflac.dll",  // FLAC
        "bassopus.dll",  // Opus / .opus
        "bass_aac.dll",  // AAC / M4A / MP4
        "bassalac.dll",  // Apple Lossless (ALAC)
        "basswma.dll",   // WMA
        "bassdsd.dll",   // DSD (.dsf / .dff)
        "bassape.dll",   // Monkey's Audio (APE)
        "basswv.dll"     // WavPack (.wv)
    };

    private readonly List<int> _loadedPluginHandles = new();
    private float _currentReplayGainScale = 1.0f;
    private float _preampGainDb = 0.0f;
    private int _currentDeviceIndex = -1;
    private string? _currentDeviceEndpointId = null;
    private string? _currentTrackUri = null;

    public event EventHandler? OutputDeviceChanged;
    public event EventHandler? PlaybackInterrupted;

    private void OnDefaultAudioEndpointChanged(string? newDefaultDeviceId)
    {
        if (_currentDeviceIndex <= 0 && string.IsNullOrWhiteSpace(_currentDeviceEndpointId))
        {
            lock (_streamLock)
            {
                if (_currentStream != 0)
                {
                    bool wasPlaying = Bass.ChannelIsActive(_currentStream) == ManagedBass.PlaybackState.Playing;
                    if (wasPlaying)
                    {
                        Debug.WriteLine($"[OCTAVE ENGINE] Default audio endpoint changed during playback (new default: {newDefaultDeviceId}). Pausing playback...");
                        Bass.ChannelPause(_currentStream);
                        RecreateStreamOnDeviceUnlocked(1, autoResume: false);
                        PlaybackInterrupted?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        RecreateStreamOnDeviceUnlocked(1, autoResume: false);
                    }
                }
            }
            WindowsAudioDeviceHelper.ClearCache();
            OutputDeviceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int ResolveBassDevice(int deviceIndex, string? driverGuid)
    {
        if (IsSilentFallback) return 0;

        // 1. If an explicit hardware device was requested, look for matching Driver GUID
        if (deviceIndex > 0 && !string.IsNullOrWhiteSpace(driverGuid))
        {
            for (int i = 1; Bass.GetDeviceInfo(i, out var bInfo); i++)
            {
                if (bInfo.IsEnabled && string.Equals(bInfo.Driver, driverGuid, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        // 2. Windows Default: resolve the real CoreAudio default endpoint ID
        string? defEndpoint = Octave.Core.Helpers.WindowsAudioDeviceHelper.GetDefaultOutputEndpointId();
        if (!string.IsNullOrWhiteSpace(defEndpoint))
        {
            for (int i = 1; Bass.GetDeviceInfo(i, out var bInfo); i++)
            {
                if (bInfo.IsEnabled && string.Equals(bInfo.Driver, defEndpoint, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        // 3. Fallback: BASS default device with actual hardware driver
        for (int i = 2; Bass.GetDeviceInfo(i, out var bInfo); i++)
        {
            if (bInfo.IsEnabled && bInfo.IsDefault)
            {
                return i;
            }
        }

        // 4. Fallback: first available enabled hardware device
        for (int i = 2; Bass.GetDeviceInfo(i, out var bInfo); i++)
        {
            if (bInfo.IsEnabled) return i;
        }

        // 5. Ultimate fallback: device 1
        return 1;
    }

    private void OnAudioEndpointsChanged()
    {
        WindowsAudioDeviceHelper.ClearCache();

        lock (_streamLock)
        {
            // 1. If user had an explicit device selected, check if it was disconnected
            if (!string.IsNullOrWhiteSpace(_currentDeviceEndpointId))
            {
                var available = GetAvailableOutputDevices();
                bool stillExists = available.Any(d => d.Index > 1 && string.Equals(d.Driver, _currentDeviceEndpointId, StringComparison.OrdinalIgnoreCase));
                if (!stillExists)
                {
                    Debug.WriteLine($"[OCTAVE ENGINE] Explicit output device '{_currentDeviceEndpointId}' was disconnected. Falling back to Windows Default...");
                    bool wasPlaying = _currentStream != 0 && Bass.ChannelIsActive(_currentStream) == ManagedBass.PlaybackState.Playing;
                    if (wasPlaying)
                    {
                        Bass.ChannelPause(_currentStream);
                        PlaybackInterrupted?.Invoke(this, EventArgs.Empty);
                    }

                    _currentDeviceIndex = -1;
                    _currentDeviceEndpointId = null;
                    int defDev = ResolveBassDevice(-1, null);
                    if (defDev > 0 && Bass.GetDeviceInfo(defDev, out var dInfo) && !dInfo.IsInitialized)
                    {
                        Bass.Init(defDev, 44100, DeviceInitFlags.Default, IntPtr.Zero);
                    }
                    RecreateStreamOnDeviceUnlocked(defDev, autoResume: false);
                    OutputDeviceChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }

            // 2. Windows Default: check if active hardware endpoint changed
            int targetBassDev = ResolveBassDevice(_currentDeviceIndex, _currentDeviceEndpointId);
            int currentStreamDev = _currentStream != 0 ? Bass.ChannelGetDevice(_currentStream) : -1;

            if (_currentStream != 0 && targetBassDev > 0 && currentStreamDev != targetBassDev)
            {
                bool wasPlaying = Bass.ChannelIsActive(_currentStream) == ManagedBass.PlaybackState.Playing;
                
                // If headphones or external DAC were unplugged while playing: pause to prevent blasting laptop speakers!
                bool wasHeadphonesOrDac = false;
                if (currentStreamDev >= 0 && Bass.GetDeviceInfo(currentStreamDev, out var oldDevInfo))
                {
                    var details = WindowsAudioDeviceHelper.GetOutputDeviceInfo(oldDevInfo.Driver);
                    wasHeadphonesOrDac = details.DeviceType == "Headphones" ||
                                         details.DeviceType.Contains("DAC") ||
                                         details.DeviceType.Contains("USB") ||
                                         details.DeviceType.Contains("Bluetooth");
                }

                if (wasPlaying && wasHeadphonesOrDac)
                {
                    Debug.WriteLine("[OCTAVE ENGINE] Headphones / DAC unplugged during playback. Pausing to prevent speaker blast.");
                    Bass.ChannelPause(_currentStream);
                    PlaybackInterrupted?.Invoke(this, EventArgs.Empty);
                    RecreateStreamOnDeviceUnlocked(targetBassDev, autoResume: false);
                }
                else
                {
                    // Seamless migration (e.g. headphones plugged in)
                    RecreateStreamOnDeviceUnlocked(targetBassDev, autoResume: wasPlaying);
                }
            }
        }

        OutputDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Init()
    {
        if (_isInitialized) return true;

        // Tells BASS to dynamically follow the default Windows output device (e.g. bluetooth/headphone disconnect).
        // Must be configured BEFORE calling Bass.Init.
        Bass.Configure(Configuration.IncludeDefaultDevice, true);
        Bass.Configure(Configuration.DevNonStop, true);

        // Attempt 1: Init with Default Windows Audio Device (-1), 44.1kHz
        IsSilentFallback = false;
        _isInitialized = Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero);
        if (!_isInitialized && Bass.LastError == Errors.Already)
        {
            _isInitialized = true;
        }

        // When IncludeDefaultDevice is enabled, device 1 is the dynamic default device entry
        if (Bass.GetDeviceInfo(1, out var dev1) && !dev1.IsInitialized)
        {
            Bass.Init(1, 44100, DeviceInitFlags.Default, IntPtr.Zero);
        }

        // Also proactively initialize the real resolved default hardware endpoint
        int realDefaultDev = ResolveBassDevice(-1, null);
        if (realDefaultDev > 0 && Bass.GetDeviceInfo(realDefaultDev, out var realDev) && !realDev.IsInitialized)
        {
            Bass.Init(realDefaultDev, 44100, DeviceInitFlags.Default, IntPtr.Zero);
        }

        if (!_isInitialized)
        {
            Debug.WriteLine($"[OCTAVE ENGINE] BASS Init (-1) Failed (Error: {Bass.LastError}). Falling back to No Sound device (0)...");
            // Attempt 2: Fallback to No Sound device (0) so engine calls remain safe
            _isInitialized = Bass.Init(0, 44100, DeviceInitFlags.Default, IntPtr.Zero);
            // AUDIO-02: the fallback keeps every engine call safe but produces NO
            // audio. Record the state instead of pretending init succeeded; Play
            // retries the real device on each call until one is available.
            IsSilentFallback = _isInitialized;
            if (IsSilentFallback)
            {
                Debug.WriteLine("[OCTAVE ENGINE] WARNING: no real output device available - running on the No Sound device; playback will be SILENT until a device appears.");
            }
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
            IsEqEngineAvailable = true;
        }
        catch (Exception ex)
        {
            IsEqEngineAvailable = false;
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
                    lock (_streamLock)
                    {
                        _loadedPluginHandles.Add(handle);
                    }
                    Debug.WriteLine($"[OCTAVE ENGINE] Decoder plugin loaded: {name}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCTAVE ENGINE] Plugin load threw for {name}: {ex.Message}");
            }
        }
    }

    private const int MaxCrossfadeDurationMs = 10000; // AUDIO-06: bound absurd fades

    private int _crossfadeDurationMs = 1000; // Default 1000ms (1 second) smooth crossfade
    public int CrossfadeDurationMs
    {
        get => _crossfadeDurationMs;
        set => _crossfadeDurationMs = Math.Clamp(value, 0, MaxCrossfadeDurationMs);
    }

    // AUDIO-02: called from Play while running on the No Sound device. A fresh
    // BASS.Init becomes the calling thread's current device, and CreateStream in
    // this same Play call runs on that same thread - so a successful recovery
    // deterministically lands the new stream on real output.
    private void TryRecoverRealDevice()
    {
        if (!IsSilentFallback) return;
        if (Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero))
        {
            IsSilentFallback = false;
            _isInitialized = true;
            Debug.WriteLine("[OCTAVE ENGINE] Output device recovered - leaving the No Sound fallback.");
        }
    }

    public long Play(string urlOrPath, double replayGain = 0.0)
    {
        // AUDIO-03: lazy Init used to run outside any lock - two concurrent Plays
        // could both enter Bass.Init (not thread-safe). Plays are serialized by
        // _playGate instead.
        lock (_playGate)
        {
            if (!_isInitialized) Init();
            else TryRecoverRealDevice(); // no-op unless we're on the silent fallback

            long sessionId = Interlocked.Increment(ref _sessionIdCounter);

            // ---- Phase 1: retire the previous stream under _streamLock ----
            // Only quick native handle calls happen here; the potentially slow
            // CreateStream is deliberately NOT in this lock (AUDIO-01).
            int fadeMs;
            bool crossfadeOutOld;
            int stopGeneration;
            lock (_streamLock)
            {
                fadeMs = _crossfadeDurationMs;
                stopGeneration = _stopGeneration;

                int oldStream = _currentStream;
                bool isOldPlaying = oldStream != 0 && Bass.ChannelIsActive(oldStream) == ManagedBass.PlaybackState.Playing;
                crossfadeOutOld = fadeMs > 0 && isOldPlaying;

                if (oldStream != 0)
                {
                    DetachEndSyncUnlocked(oldStream); // AUDIO-07: a fading/stopped stream must never raise TrackEnded
                    RemoveEqUnlocked(); // Detach EQ handles from old stream so new stream can claim FX

                    if (crossfadeOutOld)
                    {
                        FadeAndFreeStreamUnlocked(oldStream, fadeMs);
                    }
                    else
                    {
                        Bass.ChannelStop(oldStream);
                        Bass.StreamFree(oldStream);
                        _endSyncs.Remove(oldStream); // callback may not have consumed it
                    }
                    _currentStream = 0;
                }
            }

            // ---- Phase 2: create the new stream WITHOUT holding _streamLock ----
            // For HTTP sources this blocks on a network connect; holding the lock
            // here froze the position timer, FFT reads and Status/Position polls.
            int targetDev = ResolveBassDevice(_currentDeviceIndex, _currentDeviceEndpointId);
            if (!IsSilentFallback && targetDev > 0 && Bass.GetDeviceInfo(targetDev, out var dInfo) && !dInfo.IsInitialized)
            {
                Bass.Init(targetDev, 44100, DeviceInitFlags.Default, IntPtr.Zero);
            }

            try
            {
                Bass.CurrentDevice = targetDev;
            }
            catch { }

            int stream;
            if (urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                stream = Bass.CreateStream(urlOrPath, 0, BassFlags.Default, null, IntPtr.Zero);
            }
            else
            {
                stream = Bass.CreateStream(urlOrPath, 0, 0, BassFlags.Default);
            }

            // ---- Phase 3: install the new stream under _streamLock ----
            bool started = false;
            if (stream != 0)
            {
                lock (_streamLock)
                {
                    if (_stopGeneration != stopGeneration)
                    {
                        // Stop() ran while we were opening the stream - honor it:
                        // free quietly, start nothing, raise nothing.
                        Bass.StreamFree(stream);
                        return sessionId;
                    }

                    _currentTrackUri = urlOrPath;
                    _currentStream = stream;

                    // Route new stream to user's selected hardware device or dynamic default if not already on it
                    if (targetDev > 0 && Bass.GetDeviceInfo(targetDev, out var devInfo) && devInfo.IsInitialized)
                    {
                        if (Bass.ChannelGetDevice(stream) != targetDev)
                        {
                            Bass.ChannelSetDevice(stream, targetDev);
                        }
                    }

                    // AUDIO-07: per-stream End-sync identity (see _endSyncs).
                    RegisterEndSyncUnlocked(stream, sessionId, urlOrPath);

                    // Convert ReplayGain (dB) to a linear scalar (10^(dB/20)).
                    double safeReplayGain = double.IsNaN(replayGain) || double.IsInfinity(replayGain) ? 0.0 : replayGain;
                    float targetGain = (float)Math.Pow(10, safeReplayGain / 20.0);
                    _currentReplayGainScale = float.IsNaN(targetGain) || float.IsInfinity(targetGain) ? 1.0f : Math.Clamp(targetGain, 0.1f, 2.0f);

                    float safePreampDb = float.IsNaN(_preampGainDb) || float.IsInfinity(_preampGainDb) ? 0.0f : _preampGainDb;
                    float preampScale = (float)Math.Pow(10, safePreampDb / 20.0);
                    if (float.IsNaN(preampScale) || float.IsInfinity(preampScale)) preampScale = 1.0f;

                    float effectiveBaseVol = _isMuted ? 0f : (float.IsNaN(_volume) || float.IsInfinity(_volume) ? 0.5f : _volume);
                    float finalTargetVolume = Math.Clamp(effectiveBaseVol * _currentReplayGainScale * preampScale, 0f, 2.0f);

                    if (crossfadeOutOld)
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

                    // Read streaming quality (e.g. 24-bit 96.0kHz or 320 kbps 44.1kHz)
                    if (Bass.ChannelGetInfo(stream, out var info))
                    {
                        int bits = (info.OriginalResolution > 0) ? info.OriginalResolution : 16;
                        double khz = info.Frequency / 1000.0;
                        bool isLossy = info.ChannelType is ChannelType.MP3 or ChannelType.AAC or ChannelType.MP4 or ChannelType.OGG or ChannelType.WMA;

                        if (isLossy && Bass.ChannelGetAttribute(stream, ChannelAttribute.Bitrate, out float bRate) && bRate > 0)
                        {
                            StreamingQuality = $"{Math.Round(bRate)} kbps {khz:0.0}kHz";
                        }
                        else
                        {
                            StreamingQuality = $"{bits}-bit {khz:0.0}kHz";
                        }
                    }
                    else
                    {
                        StreamingQuality = "Unknown";
                    }

                    started = true;
                    Debug.WriteLine($"[OCTAVE ENGINE] Playing stream ID: {stream}, Session ID: {sessionId} (Crossfade: {fadeMs}ms)");
                }
            }
            else
            {
                Debug.WriteLine($"[OCTAVE ENGINE] Stream creation failed! Path: {urlOrPath}, BASS Error: {Bass.LastError}");
            }

            if (started)
            {
                // Start periodic position reporting and notify listeners OUTSIDE
                // _streamLock (load-bearing invariant - see _streamLock comment).
                StartPositionTimer();
                // NF-32: a throwing UI handler runs on THIS caller's thread - it
                // must never be able to kill playback or the process.
                try
                {
                    TrackStarted?.Invoke(this, urlOrPath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OCTAVE ENGINE] TrackStarted handler failed: {ex.Message}");
                }
            }
            else if (stream == 0)
            {
                // On stream load failure (corrupted file, unsupported format),
                // auto-advance queue off-thread with THIS call's session identity.
                var handler = TrackEnded;
                if (handler != null)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            handler.Invoke(this, new TrackEndedEventArgs(sessionId, urlOrPath, isNaturalEnd: false));
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[OCTAVE ENGINE] TrackEnded handler failed: {ex.Message}");
                        }
                    });
                }
            }

            return sessionId;
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
                resumed = Bass.ChannelPlay(_currentStream, false);
                if (!resumed)
                {
                    Debug.WriteLine($"[OCTAVE ENGINE] ChannelPlay failed on Resume (Error: {Bass.LastError}). Recovering stream on current device...");
                    int targetDev = _currentDeviceIndex <= 0 ? 1 : _currentDeviceIndex;
                    RecreateStreamOnDeviceUnlocked(targetDev);
                    resumed = _currentStream != 0 && Bass.ChannelIsActive(_currentStream) == ManagedBass.PlaybackState.Playing;
                }
            }
        }
        if (resumed) StartPositionTimer();
    }

    // Slides a superseded stream to silence and frees it after the fade. The
    // caller MUST hold _streamLock (Play/Stop call this from inside their lock
    // section); only the deferred free re-acquires it on a pool thread.
    private void FadeAndFreeStreamUnlocked(int streamToFree, int fadeMs)
    {
        if (streamToFree == 0) return;

        _fadingStreams.Add(streamToFree);

        Bass.ChannelSlideAttribute(streamToFree, ChannelAttribute.Volume, 0f, fadeMs);
        Task.Run(async () =>
        {
            await Task.Delay(fadeMs + 100);
            lock (_streamLock)
            {
                if (_disposed) return;
                if (_fadingStreams.Remove(streamToFree))
                {
                    Bass.ChannelStop(streamToFree);
                    Bass.StreamFree(streamToFree);
                    _endSyncs.Remove(streamToFree);
                }
            }
        });
    }

    public void Stop()
    {
        StopPositionTimer();
        lock (_streamLock)
        {
            // Abort any in-flight Play whose stream is still being created
            // outside the lock: it will see the new generation and free quietly.
            _stopGeneration++;

            if (_currentStream != 0)
            {
                int streamToStop = _currentStream;
                _currentStream = 0;
                DetachEndSyncUnlocked(streamToStop); // AUDIO-07: a fading/stopped stream must never raise TrackEnded
                RemoveEqUnlocked();

                if (_crossfadeDurationMs > 0 && Bass.ChannelIsActive(streamToStop) == ManagedBass.PlaybackState.Playing)
                {
                    int fadeMs = Math.Min(_crossfadeDurationMs, 500); // 500ms quick fade out on explicit stop
                    FadeAndFreeStreamUnlocked(streamToStop, fadeMs);
                }
                else
                {
                    Bass.ChannelStop(streamToStop);
                    Bass.StreamFree(streamToStop);
                    _endSyncs.Remove(streamToStop);
                }
            }
        }
    }

    // ---- Equalizer --------------------------------------------------------

    public IReadOnlyList<int> EqFrequencies => EqFreqs;

    // UI-ST-04: probed once during Init - bass_fx.dll is native, so a missing
    // file surfaces as a P/Invoke failure the first time BassFx is touched.
    public bool IsEqEngineAvailable { get; private set; }

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
        gainDb = float.IsNaN(gainDb) || float.IsInfinity(gainDb) ? 0f : Math.Clamp(gainDb, -15f, 15f);
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
        _preampGainDb = float.IsNaN(gainDb) || float.IsInfinity(gainDb) ? 0.0f : Math.Clamp(gainDb, -15f, 15f);
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
            // AUDIO-05: BASS applies higher-priority FX FIRST. The EQ must run
            // before the limiter so the limiter sits last in the chain and can
            // catch clipping from up-to-+15 dB EQ boosts (the old order had the
            // compressor ahead of the EQ, where it couldn't).
            if (_eqFxHandle == 0)
                _eqFxHandle = Bass.ChannelSetFX(_currentStream, EffectType.PeakEQ, 1);

            if (_limiterFxHandle == 0)
                _limiterFxHandle = Bass.ChannelSetFX(_currentStream, EffectType.Compressor, 0);

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
            // AUDIO-06: the ISO center frequencies are 1-octave apart; a 2.5-octave
            // bandwidth made adjacent bands overlap heavily. Match the spacing.
            fBandwidth = 1.0f,
            fCenter = EqFreqs[index],
            fGain = _eqGains[index],
            lBand = index,
            // NF-21: BASS_BFX_CHANALL (-1). Left at its default this field is
            // None(0), which matches NO channel mask - every band was configured
            // correctly and still processed zero channels, so the EQ was silent.
            lChannel = FXChannelFlags.All
        };
        if (!Bass.FXSetParameters(_eqFxHandle, p))
            Debug.WriteLine($"[OCTAVE ENGINE] EQ band {index} apply failed (Error: {Bass.LastError})");
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
            float safePreampDb = float.IsNaN(_preampGainDb) || float.IsInfinity(_preampGainDb) ? 0.0f : _preampGainDb;
            float preampScale = (float)Math.Pow(10, safePreampDb / 20.0);
            if (float.IsNaN(preampScale) || float.IsInfinity(preampScale)) preampScale = 1.0f;

            float baseVol = _isMuted ? 0f : (float.IsNaN(_volume) || float.IsInfinity(_volume) ? 0.5f : _volume);
            float effectiveVolume = Math.Clamp(baseVol * _currentReplayGainScale * preampScale, 0f, 2f);
            Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, effectiveVolume);
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            float clamped = float.IsNaN(value) || float.IsInfinity(value) ? 0.5f : Math.Clamp(value, 0f, 1f);
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
            var (name, _, _, _, _) = Octave.Core.Helpers.WindowsAudioDeviceHelper.GetOutputDeviceInfo(_currentDeviceEndpointId, forceRefresh: true);
            if (!string.IsNullOrWhiteSpace(name) && name != "Default Audio Device") return name;

            if (_isInitialized)
            {
                int currentDev = _currentDeviceIndex >= 0 ? _currentDeviceIndex : Bass.CurrentDevice;
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
            var (_, format, _, _, _) = Octave.Core.Helpers.WindowsAudioDeviceHelper.GetOutputDeviceInfo(_currentDeviceEndpointId, forceRefresh: true);
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
            var (devName, devFormat, devKhz, devBits, devType) = Octave.Core.Helpers.WindowsAudioDeviceHelper.GetOutputDeviceInfo(_currentDeviceEndpointId, forceRefresh: true);
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
            int? bitrateKbps = null;
            bool isLossy = false;

            lock (_streamLock)
            {
                if (_currentStream != 0 && Bass.ChannelGetInfo(_currentStream, out var info))
                {
                    bits = (info.OriginalResolution > 0) ? info.OriginalResolution : 16;
                    sourceKhz = info.Frequency / 1000.0;
                    channels = info.Channels;

                    isLossy = info.ChannelType is ChannelType.MP3 or ChannelType.AAC or ChannelType.MP4 or ChannelType.OGG or ChannelType.WMA;

                    if (Bass.ChannelGetAttribute(_currentStream, ChannelAttribute.Bitrate, out float bRate) && bRate > 0)
                    {
                        bitrateKbps = (int)Math.Round(bRate);
                    }

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

            bool isBitMatched = Math.Abs(sourceKhz - devKhz) < 0.1 && !_eqEnabled && Math.Abs(_currentReplayGainScale - 1.0f) < 0.01f && Math.Abs(_preampGainDb) < 0.01f;

            string resamplingStatus;
            if (isBitMatched)
            {
                resamplingStatus = $"Bit-Matched Direct Target ({sourceKhz:0.0}kHz)";
            }
            else if (Math.Abs(sourceKhz - devKhz) < 0.1)
            {
                resamplingStatus = $"Bit-Matched Sample Rate ({sourceKhz:0.0}kHz)";
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
            else if (!isLossy && bits == 16 && sourceKhz >= 44.1)
            {
                qualityBadgeType = "CDQuality";
            }
            else if (isLossy)
            {
                qualityBadgeType = "Compressed";
            }

            string gainText = Math.Abs(_currentReplayGainScale - 1.0f) > 0.01f
                ? $"ReplayGain ({(20.0 * Math.Log10(_currentReplayGainScale)):+0.0;-0.0;0.0}dB)"
                : "Standard Level";
            string preampText = Math.Abs(_preampGainDb) > 0.01f ? $" | Preamp: {_preampGainDb:+0.0;-0.0;0.0}dB" : "";
            string dspStatus = _eqEnabled ? $"10-Band EQ Active ({gainText}{preampText})" : $"Direct Passthrough ({gainText}{preampText})";

            string streamQualityStr = (isLossy && bitrateKbps.HasValue)
                ? $"{bitrateKbps.Value} kbps {sourceKhz:0.0}kHz"
                : $"{bits}-bit {sourceKhz:0.0}kHz";

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
                DspStatus: dspStatus,
                OutputDeviceType: devType,
                IsBitMatched: isBitMatched,
                BitrateKbps: bitrateKbps
            );
        }
    }

    public int CurrentOutputDeviceIndex => _currentDeviceIndex;
    public string? CurrentOutputDeviceId => _currentDeviceEndpointId;

    public IReadOnlyList<AudioOutputDeviceInfo> GetAvailableOutputDevices()
    {
        Octave.Core.Helpers.WindowsAudioDeviceHelper.ClearCache();
        var list = new List<AudioOutputDeviceInfo>();

        // 1. Always include Windows Default (Follow System)
        var defaultInfo = Octave.Core.Helpers.WindowsAudioDeviceHelper.GetOutputDeviceInfo(null, forceRefresh: true);
        list.Add(new AudioOutputDeviceInfo(
            Index: -1,
            Id: "__default__",
            Name: $"Windows Default ({defaultInfo.Name})",
            Driver: "",
            DeviceType: defaultInfo.DeviceType,
            IsDefault: true,
            IsEnabled: true
        ));

        // 2. Hardware devices from BASS
        try
        {
            for (int i = 1; Bass.GetDeviceInfo(i, out var bInfo); i++)
            {
                if (!bInfo.IsEnabled) continue;
                // When IncludeDefaultDevice is enabled, device 1 is the duplicate mirror of "Default".
                // Skip it so users only see the canonical -1 Windows Default and the real named hardware endpoints.
                if (string.IsNullOrEmpty(bInfo.Driver) && (bInfo.Name == "Default" || bInfo.Name == "Default device")) continue;

                var devInfo = Octave.Core.Helpers.WindowsAudioDeviceHelper.GetOutputDeviceInfo(bInfo.Driver, forceRefresh: true);
                string name = !string.IsNullOrWhiteSpace(bInfo.Name) ? bInfo.Name : devInfo.Name;
                string type = devInfo.DeviceType;

                list.Add(new AudioOutputDeviceInfo(
                    Index: i,
                    Id: bInfo.Driver ?? i.ToString(),
                    Name: name,
                    Driver: bInfo.Driver ?? "",
                    DeviceType: type,
                    IsDefault: bInfo.IsDefault,
                    IsEnabled: bInfo.IsEnabled
                ));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OCTAVE ENGINE] GetAvailableOutputDevices failed: {ex.Message}");
        }

        return list;
    }

    public void SetOutputDevice(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId == "__default__")
        {
            SetOutputDeviceInternal(-1, null);
            return;
        }

        // Search BASS devices for matching Driver GUID
        for (int i = 1; Bass.GetDeviceInfo(i, out var bInfo); i++)
        {
            if (string.Equals(bInfo.Driver, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                SetOutputDeviceInternal(i, bInfo.Driver);
                return;
            }
        }

        // If not found (e.g. unplugged), fallback to default
        SetOutputDeviceInternal(-1, null);
    }

    public void SetOutputDevice(int deviceIndex)
    {
        if (deviceIndex <= 0)
        {
            SetOutputDeviceInternal(-1, null);
        }
        else if (Bass.GetDeviceInfo(deviceIndex, out var bInfo))
        {
            SetOutputDeviceInternal(deviceIndex, bInfo.Driver);
        }
        else
        {
            SetOutputDeviceInternal(-1, null);
        }
    }

    private void SetOutputDeviceInternal(int deviceIndex, string? driverGuid)
    {
        int normalizedIndex = deviceIndex <= 0 ? -1 : deviceIndex;
        string? normalizedGuid = normalizedIndex == -1 ? null : driverGuid;

        lock (_streamLock)
        {
            if (_currentDeviceIndex == normalizedIndex && string.Equals(_currentDeviceEndpointId, normalizedGuid, StringComparison.OrdinalIgnoreCase))
            {
                // Already targeting this exact device. Do not recreate streams or fire redundant events.
                return;
            }
        }

        try
        {
            int targetBassDev = ResolveBassDevice(normalizedIndex, normalizedGuid);

            if (!IsSilentFallback && targetBassDev > 0 && Bass.GetDeviceInfo(targetBassDev, out var dInfo) && !dInfo.IsInitialized)
            {
                Bass.Init(targetBassDev, 44100, DeviceInitFlags.Default, IntPtr.Zero);
            }

            lock (_streamLock)
            {
                _currentDeviceIndex = normalizedIndex;
                _currentDeviceEndpointId = normalizedGuid;

                if (_currentStream != 0 && targetBassDev > 0)
                {
                    bool wasPlaying = Bass.ChannelIsActive(_currentStream) == ManagedBass.PlaybackState.Playing;
                    RecreateStreamOnDeviceUnlocked(targetBassDev, autoResume: wasPlaying);
                }
            }

            if (Bass.GetDeviceInfo(targetBassDev, out var postInfo) && postInfo.IsInitialized)
            {
                try { Bass.CurrentDevice = targetBassDev; } catch { }
            }

            Octave.Core.Helpers.WindowsAudioDeviceHelper.ClearCache();
            OutputDeviceChanged?.Invoke(this, EventArgs.Empty);
            Debug.WriteLine($"[OCTAVE ENGINE] Output device changed to index {normalizedIndex} (Resolved BASS: {targetBassDev}, Endpoint: {_currentDeviceEndpointId ?? "Default"})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OCTAVE ENGINE] SetOutputDeviceInternal failed: {ex.Message}");
        }
    }

    private void RecreateStreamOnDeviceUnlocked(int targetBassDev, bool autoResume = true)
    {
        if (string.IsNullOrEmpty(_currentTrackUri)) return;

        double currentPos = GetPositionSeconds();
        bool wasPlaying = _currentStream != 0 && Bass.ChannelIsActive(_currentStream) == ManagedBass.PlaybackState.Playing;

        if (_currentStream != 0)
        {
            DetachEndSyncUnlocked(_currentStream);
            RemoveEqUnlocked();
            Bass.ChannelStop(_currentStream);
            Bass.StreamFree(_currentStream);
            _currentStream = 0;
        }

        if (targetBassDev > 0 && (!Bass.GetDeviceInfo(targetBassDev, out var devInit) || !devInit.IsInitialized))
        {
            Bass.Init(targetBassDev, 44100, DeviceInitFlags.Default, IntPtr.Zero);
        }

        if (targetBassDev > 0 && Bass.GetDeviceInfo(targetBassDev, out var devPost) && devPost.IsInitialized)
        {
            try { Bass.CurrentDevice = targetBassDev; } catch { }
        }

        int newStream;
        if (_currentTrackUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            _currentTrackUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            newStream = Bass.CreateStream(_currentTrackUri, 0, BassFlags.Default, null, IntPtr.Zero);
        }
        else
        {
            newStream = Bass.CreateStream(_currentTrackUri, 0, 0, BassFlags.Default);
        }

        if (newStream != 0)
        {
            _currentStream = newStream;
            if (targetBassDev > 0 && Bass.GetDeviceInfo(targetBassDev, out var devChk) && devChk.IsInitialized)
            {
                if (Bass.ChannelGetDevice(_currentStream) != targetBassDev)
                {
                    Bass.ChannelSetDevice(_currentStream, targetBassDev);
                }
            }
            RegisterEndSyncUnlocked(_currentStream, _sessionIdCounter, _currentTrackUri);
            UpdateStreamVolumeUnlocked();
            if (_eqEnabled) SetupEqUnlocked();
            if (currentPos > 0)
            {
                long bytePos = Bass.ChannelSeconds2Bytes(_currentStream, currentPos);
                Bass.ChannelSetPosition(_currentStream, bytePos);
            }
            if (wasPlaying && autoResume)
            {
                Bass.ChannelPlay(_currentStream, false);
            }
            Debug.WriteLine($"[OCTAVE ENGINE] Stream successfully recreated on device {targetBassDev} at position {currentPos:0.0}s (autoResume={autoResume})");
        }
    }

    private readonly float[] _rawFftBuffer = new float[256];
    private float[]? _lastFftPeaks;

    public float[] GetFftData(int binCount = 36)
    {
        if (binCount <= 0) binCount = 36;
        var result = new float[binCount];

        lock (_streamLock)
        {
            // AUDIO-06: resize the peak buffer under the lock (it used to be
            // swapped in before taking it - benign today, wrong in principle).
            if (_lastFftPeaks == null || _lastFftPeaks.Length != binCount)
            {
                _lastFftPeaks = new float[binCount];
            }

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

            AggregateFftBins(_rawFftBuffer, binCount, _lastFftPeaks, result);
        }

        return result;
    }

    // TEST-06: the logarithmic bin mapping + attack/decay peak smoothing, extracted
    // from GetFftData as a pure function so tests can inject a synthetic raw FFT
    // buffer (BASS FFT512 → 256 magnitude bins) and assert exact per-bin outputs.
    // Previously the test could only observe the array length. `peaks` carries the
    // previous frame's state and is updated in place; `result` receives the display
    // values. Caller owns any locking.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int Start, int End)[]> BinMapCache = new();

    internal static (int Start, int End)[] GetBinMap(int binCount)
    {
        return BinMapCache.GetOrAdd(binCount, count =>
        {
            var map = new (int Start, int End)[count];
            for (int i = 0; i < count; i++)
            {
                int startBin = (int)Math.Pow(256.0, (double)i / count);
                int endBin = (int)Math.Pow(256.0, (double)(i + 1) / count);
                startBin = Math.Clamp(startBin, 0, 255);
                endBin = Math.Clamp(endBin, startBin + 1, 256);
                map[i] = (startBin, endBin);
            }
            return map;
        });
    }

    internal static void AggregateFftBins(float[] rawFft, int binCount, float[] peaks, float[] result)
    {
        var binMap = GetBinMap(binCount);
        for (int i = 0; i < binCount; i++)
        {
            var (startBin, endBin) = binMap[i];

            float maxVal = 0f;
            for (int b = startBin; b < endBin; b++)
            {
                if (rawFft[b] > maxVal) maxVal = rawFft[b];
            }

            float target = Math.Clamp((float)(Math.Sqrt(maxVal) * 1.8), 0.02f, 1.0f);
            if (target > peaks[i])
            {
                peaks[i] = peaks[i] * 0.3f + target * 0.7f;
            }
            else
            {
                peaks[i] = peaks[i] * 0.82f + target * 0.18f;
            }

            result[i] = peaks[i];
        }
    }

    public void Seek(double positionSeconds)
    {
        lock (_streamLock)
        {
            if (_currentStream == 0) return;
            double duration = Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetLength(_currentStream));
            if (double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0)
            {
                duration = 0.0;
            }

            double safePos = double.IsNaN(positionSeconds) || double.IsInfinity(positionSeconds)
                ? 0.0
                : Math.Max(0.0, positionSeconds);

            double clampedPosition = (duration > 0) ? Math.Clamp(safePos, 0.0, duration) : 0.0;
            long bytePosition = Bass.ChannelSeconds2Bytes(_currentStream, clampedPosition);
            Bass.ChannelSetPosition(_currentStream, bytePosition);
        }
    }

    // Caller MUST hold _streamLock. Registers the natural-end sync together with
    // THIS stream's identity; the callback resolves identity from its own
    // `channel` argument instead of reading global state (AUDIO-07).
    private void RegisterEndSyncUnlocked(int stream, long sessionId, string uri)
    {
        // Standard SyncFlags.End fires after the full buffer has completed audible playback
        // (NOT SyncFlags.Mixtime, which fires prematurely during decoding and causes deadlock
        // when BASS holds internal mixer locks).
        int syncHandle = Bass.ChannelSetSync(stream, SyncFlags.End, 0, _endSyncCallback, IntPtr.Zero);
        if (syncHandle != 0)
        {
            _endSyncs[stream] = new EndSyncIdentity(syncHandle, sessionId, uri);
        }
        else
        {
            // Without the sync a natural end cannot auto-advance the queue.
            Debug.WriteLine($"[OCTAVE ENGINE] Failed to register End sync for stream {stream}! Error: {Bass.LastError}");
        }
    }

    // Caller MUST hold _streamLock. Removes a stream's End-sync so a superseded /
    // crossfaded-out stream can never raise TrackEnded at all (AUDIO-07).
    private void DetachEndSyncUnlocked(int stream)
    {
        if (_endSyncs.Remove(stream, out var identity))
        {
            Bass.ChannelRemoveSync(stream, identity.SyncHandle);
        }
    }

    private void OnTrackEndedCallback(int handle, int channel, int data, IntPtr user)
    {
        // This executes on BASS's unmanaged sync thread. Freeing the stream or
        // starting the next track from here (which the auto-advance does) is
        // unsafe and can deadlock the engine. Stop the timer and marshal the
        // TrackEnded notification onto a thread-pool thread so the callback can
        // return immediately and the queue advances on a clean managed thread.
        //
        // NF-32: an exception escaping a native BASS callback cannot unwind
        // through the pinvoke boundary - it surfaces as a fatal stowed exception
        // (0xC000027B). Nothing in here may throw outward.
        try
        {
            EndSyncIdentity identity;
            lock (_streamLock)
            {
                // AUDIO-07: resolve identity from the channel that ACTUALLY ended,
                // not from the globals. A stream whose sync we already detached
                // (manual skip / crossfade / stop) has no entry here - its late end
                // is dropped instead of masquerading as the incoming track's end.
                if (!_endSyncs.Remove(channel, out identity))
                {
                    return;
                }
            }

            StopPositionTimer();

            var handler = TrackEnded;
            if (handler != null)
            {
                // Copy before leaving the callback scope; raise OUTSIDE _streamLock
                // (load-bearing invariant - see _streamLock comment).
                long endedSession = identity.SessionId;
                string endedUri = identity.Uri;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        handler.Invoke(this, new TrackEndedEventArgs(endedSession, endedUri));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OCTAVE ENGINE] TrackEnded handler failed: {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OCTAVE ENGINE] End-sync callback failed: {ex.Message}");
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

        // NF-32: this runs on a timer thread - a throwing subscriber would end
        // the process via AppDomain.UnhandledException.
        try
        {
            PositionChanged?.Invoke(this, pos);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OCTAVE ENGINE] PositionChanged handler failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        WindowsAudioDeviceHelper.AudioEndpointsChanged -= OnAudioEndpointsChanged;
        WindowsAudioDeviceHelper.DefaultAudioEndpointChanged -= OnDefaultAudioEndpointChanged;
        StopPositionTimer();
        lock (_streamLock)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var fading in _fadingStreams)
            {
                Bass.ChannelStop(fading);
                Bass.StreamFree(fading);
            }
            _fadingStreams.Clear();
            _endSyncs.Clear();

            // Free BASS stream if open
            if (_currentStream != 0)
            {
                RemoveEqUnlocked();
                Bass.ChannelStop(_currentStream);
                Bass.StreamFree(_currentStream);
                _currentStream = 0;
            }

            // Free loaded BASS plugins
            foreach (var plugin in _loadedPluginHandles)
            {
                Bass.PluginFree(plugin);
            }
            _loadedPluginHandles.Clear();

            // Free BASS device context
            if (_isInitialized)
            {
                Bass.Free();
                _isInitialized = false;
            }
        }

        // AUDIO-04: the finalizer is gone. Native teardown used to also run from
        // the finalizer thread - taking _streamLock there and calling into BASS
        // from a finalizer risks native re-entry, and Bass.Free() from an
        // undisposed instance could tear down the shared engine under unrelated
        // work (TEST-17 pairs with this; B15 adds the explicit-dispose test sweep).
        // This service is an app-lifetime singleton: teardown is deterministic only.
    }
}
