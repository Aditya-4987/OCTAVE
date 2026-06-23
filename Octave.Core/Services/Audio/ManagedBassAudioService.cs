using ManagedBass;
using System;
using System.Diagnostics;
using System.Threading;
using Octave.Core.Models;

namespace Octave.Core.Services.Audio;

public class ManagedBassAudioService : IAudioPlayerService, IDisposable
{
    private int _currentStream = 0;
    private bool _isInitialized = false;
    private bool _disposed = false;

    private readonly SyncProcedure _endSyncCallback;
    private Timer? _positionTimer;
    private readonly object _timerLock = new();

    public event EventHandler<string>? TrackStarted;
    public event EventHandler? TrackEnded;
    public event EventHandler<double>? PositionChanged;

    public ManagedBassAudioService()
    {
        _endSyncCallback = OnTrackEndedCallback;
    }

    public bool Init()
    {
        if (_isInitialized) return true;

        // Init: -1 means "Default Windows Audio Device", 44.1kHz
        _isInitialized = Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero);
        
        if (!_isInitialized)
        {
            Debug.WriteLine($"[OCTAVE ENGINE] BASS Init Failed. Error: {Bass.LastError}");
        }
        return _isInitialized;
    }

    public void Play(string urlOrPath)
    {
        if (!_isInitialized) Init();

        Stop(); // Kill any currently playing track

        // Check if we were handed an HTTP web stream or a local hard drive path
        if (urlOrPath.StartsWith("http://") || urlOrPath.StartsWith("https://"))
        {
            _currentStream = Bass.CreateStream(urlOrPath, 0, BassFlags.Default, null, IntPtr.Zero);
        }
        else
        {
            _currentStream = Bass.CreateStream(urlOrPath, 0, 0, BassFlags.Default);
        }

        if (_currentStream != 0)
        {
            // Register end sync procedure
            Bass.ChannelSetSync(_currentStream, SyncFlags.End, 0, _endSyncCallback, IntPtr.Zero);

            Bass.ChannelPlay(_currentStream);
            Debug.WriteLine($"[OCTAVE ENGINE] Playing stream ID: {_currentStream}");

            // Start periodic position reporting
            StartPositionTimer();

            // Notify listeners that a track has successfully started
            TrackStarted?.Invoke(this, urlOrPath);
        }
        else
        {
            Debug.WriteLine($"[OCTAVE ENGINE] Stream creation failed! BASS Error: {Bass.LastError}");
        }
    }

    public void Pause()
    {
        if (_currentStream != 0)
        {
            Bass.ChannelPause(_currentStream);
            StopPositionTimer();
        }
    }
    
    public void Resume()
    {
        if (_currentStream != 0)
        {
            Bass.ChannelPlay(_currentStream);
            StartPositionTimer();
        }
    }

    public void Stop()
    {
        StopPositionTimer();
        if (_currentStream != 0)
        {
            Bass.ChannelStop(_currentStream);
            Bass.StreamFree(_currentStream);
            _currentStream = 0;
        }
    }

    public double GetDurationSeconds() => 
        Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetLength(_currentStream));

    public double GetPositionSeconds() => 
        Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetPosition(_currentStream));

    public void SetVolume(float volume) => 
        Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, Math.Clamp(volume, 0f, 1f));

    public double PositionSeconds => _currentStream != 0 
        ? Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetPosition(_currentStream)) 
        : 0.0;

    public double DurationSeconds => _currentStream != 0 
        ? Bass.ChannelBytes2Seconds(_currentStream, Bass.ChannelGetLength(_currentStream)) 
        : 0.0;

    public PlaybackStatus Status
    {
        get
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

    public void Seek(double positionSeconds)
    {
        if (_currentStream == 0) return;
        double clampedPosition = Math.Clamp(positionSeconds, 0, DurationSeconds);
        long bytePosition = Bass.ChannelSeconds2Bytes(_currentStream, clampedPosition);
        Bass.ChannelSetPosition(_currentStream, bytePosition);
    }

    private void OnTrackEndedCallback(int handle, int channel, int data, IntPtr user)
    {
        StopPositionTimer();
        TrackEnded?.Invoke(this, EventArgs.Empty);
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
        if (_currentStream != 0 && Bass.ChannelIsActive(_currentStream) == ManagedBass.PlaybackState.Playing)
        {
            double pos = GetPositionSeconds();
            PositionChanged?.Invoke(this, pos);
        }
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

            _disposed = true;
        }
    }

    ~ManagedBassAudioService()
    {
        Dispose(false);
    }
}