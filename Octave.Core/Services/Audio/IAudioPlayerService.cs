using System;
using Octave.Core.Models;

namespace Octave.Core.Services.Audio;

public interface IAudioPlayerService
{
    event EventHandler<string>? TrackStarted;
    event EventHandler? TrackEnded;
    event EventHandler<double>? PositionChanged;

    bool Init();
    void Play(string urlOrPath);
    void Pause();
    void Resume();
    void Stop();
    double GetPositionSeconds();
    double GetDurationSeconds();
    void SetVolume(float volume); // Accepts 0.0f to 1.0f

    double PositionSeconds { get; }
    double DurationSeconds { get; }
    PlaybackStatus Status { get; }
    void Seek(double positionSeconds);
}