using System;
using Octave.Core.Models;

namespace Octave.Core.Services.Audio;

public class TrackEndedEventArgs : EventArgs
{
    public long SessionId { get; }
    public string SourceUri { get; }

    public TrackEndedEventArgs(long sessionId, string sourceUri)
    {
        SessionId = sessionId;
        SourceUri = sourceUri;
    }
}

public interface IAudioPlayerService
{
    event EventHandler<string>? TrackStarted;
    event EventHandler<TrackEndedEventArgs>? TrackEnded;
    event EventHandler<double>? PositionChanged;

    bool Init();
    long Play(string urlOrPath, double replayGain = 0.0);
    void Pause();
    void Resume();
    void Stop();
    double GetPositionSeconds();
    double GetDurationSeconds();
    float Volume { get; set; } // Accepts 0.0f to 1.0f scale
    bool IsMuted { get; }
    void SetMuted(bool isMuted);
    void ToggleMute();

    double PositionSeconds { get; }
    double DurationSeconds { get; }
    PlaybackStatus Status { get; }
    string StreamingQuality { get; }
    void Seek(double positionSeconds);

    // 10-band graphic equalizer (requires bass_fx.dll at runtime).
    System.Collections.Generic.IReadOnlyList<int> EqFrequencies { get; }
    bool IsEqEnabled { get; }
    void SetEqEnabled(bool enabled);
    void SetEqBand(int index, float gainDb);
    float[] GetEqGains();
}