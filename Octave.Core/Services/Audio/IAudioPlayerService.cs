namespace Octave.Core.Services.Audio;

public interface IAudioPlayerService
{
    bool Init();
    void Play(string urlOrPath);
    void Pause();
    void Resume();
    void Stop();
    double GetPositionSeconds();
    double GetDurationSeconds();
    void SetVolume(float volume); // Accepts 0.0f to 1.0f
}