using ManagedBass;
using System.Diagnostics;

namespace Octave.Core.Services.Audio;

public class ManagedBassAudioService : IAudioPlayerService
{
    private int _currentStream = 0;
    private bool _isInitialized = false;

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
            Bass.ChannelPlay(_currentStream);
            Debug.WriteLine($"[OCTAVE ENGINE] Playing stream ID: {_currentStream}");
        }
        else
        {
            Debug.WriteLine($"[OCTAVE ENGINE] Stream creation failed! BASS Error: {Bass.LastError}");
        }
    }

    public void Pause() => Bass.ChannelPause(_currentStream);
    
    public void Resume() => Bass.ChannelPlay(_currentStream);

    public void Stop()
    {
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
}