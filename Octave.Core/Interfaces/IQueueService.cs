using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Interfaces;

public interface IQueueService
{
    event EventHandler<PlaybackState>? PlaybackStateChanged;
    event EventHandler? QueueChanged;

    // Lightweight, high-frequency position updates (seconds). Kept separate from
    // PlaybackStateChanged so the 4x/sec playback ticks don't rebroadcast the
    // entire state to every subscriber.
    event EventHandler<double>? PositionChanged;

    PlaybackState CurrentState { get; }

    IReadOnlyList<QueueItem> GetCurrentQueue();
    void Enqueue(Track track);
    void EnqueueRange(IEnumerable<Track> tracks);
    void EnqueueNext(Track track);
    void PlayIndex(int index);
    void RemoveAt(int index);
    void Clear(bool keepCurrentTrack = false);
    void Reorder(int oldIndex, int newIndex);
    void SetShuffle(bool enable);
    void SetRepeatMode(RepeatMode mode);
    void PlayNext();
    void PlayPrevious();

    // Playback control wrappers
    void Pause();
    void Resume();
    void SetVolume(float volume);
    PlaybackState Seek(double positionSeconds);

    bool RestorePositionOnStartup { get; set; }

    // Rebuild the queue from the last persisted snapshot (paused, ready to resume).
    Task RestoreAsync();
}
