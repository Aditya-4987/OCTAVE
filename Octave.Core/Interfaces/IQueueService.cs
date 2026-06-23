using System;
using System.Collections.Generic;
using Octave.Core.Models;

namespace Octave.Core.Interfaces;

public interface IQueueService
{
    event EventHandler<PlaybackState>? PlaybackStateChanged;

    PlaybackState CurrentState { get; }

    IReadOnlyList<QueueItem> GetCurrentQueue();
    void Enqueue(Track track);
    void EnqueueNext(Track track);
    void PlayIndex(int index);
    void RemoveAt(int index);
    void Clear();
    void Reorder(int oldIndex, int newIndex);
    void SetShuffle(bool enable);
    void SetRepeatMode(RepeatMode mode);
    void PlayNext();
    void PlayPrevious();

    // Playback control wrappers
    void Pause();
    void Resume();
    void SetVolume(float volume);
}
