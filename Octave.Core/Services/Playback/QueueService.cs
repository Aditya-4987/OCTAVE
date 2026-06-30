using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Audio;
using Octave.Core.Services.Database;

namespace Octave.Core.Services.Playback;

public class QueueService : IQueueService
{
    public event EventHandler<PlaybackState>? PlaybackStateChanged;
    public event EventHandler<double>? PositionChanged;

    public PlaybackState CurrentState { get; private set; }

    private readonly IAudioPlayerService _audioPlayer;
    private readonly SqliteDbContext _dbContext;

    private readonly object _queueLock = new();
    private readonly List<QueueItem> _unshuffledQueue = new();
    private readonly List<QueueItem> _activeQueue = new();

    private int _currentIndex = -1;
    private bool _isShuffle = false;
    private RepeatMode _repeatMode = RepeatMode.None;

    private float _volume = 1.0f;
    private bool _isMuted = false;
    private long _sequenceToken = 0;

    public QueueService(IAudioPlayerService audioPlayer, SqliteDbContext dbContext)
    {
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

        CurrentState = GetCurrentState();

        // Auto-advance loop subscription with required exception trapping
        _audioPlayer.TrackEnded += async (s, e) =>
        {
            try
            {
                await HandleTrackEndedAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QueueService] Auto-advance failure: {ex}");
            }
        };

        _audioPlayer.TrackStarted += (s, path) =>
        {
            lock (_queueLock)
            {
                EmitPlaybackStateChanged();
            }
        };

        // Forward the high-frequency position ticks as a lightweight event only.
        // Rebroadcasting the whole PlaybackState 4x/sec to every subscriber (and
        // marshaling each to the UI thread) was needless churn.
        _audioPlayer.PositionChanged += (s, pos) => PositionChanged?.Invoke(this, pos);
    }

    public IReadOnlyList<QueueItem> GetCurrentQueue()
    {
        lock (_queueLock)
        {
            return new List<QueueItem>(_activeQueue).AsReadOnly();
        }
    }

    public void Enqueue(Track track)
    {
        lock (_queueLock)
        {
            var item = new QueueItem
            {
                Id = Guid.NewGuid().ToString(),
                Track = track,
                IsPlaying = false
            };

            _unshuffledQueue.Add(item);
            _activeQueue.Add(item);

            EmitPlaybackStateChanged();
        }
    }

    public void EnqueueRange(IEnumerable<Track> tracks)
    {
        if (tracks == null) return;

        lock (_queueLock)
        {
            bool added = false;
            foreach (var track in tracks)
            {
                var item = new QueueItem
                {
                    Id = Guid.NewGuid().ToString(),
                    Track = track,
                    IsPlaying = false
                };

                _unshuffledQueue.Add(item);
                _activeQueue.Add(item);
                added = true;
            }

            // Emit a single state change for the whole batch instead of one per
            // track - bulk-loading a large library used to fire thousands of
            // broadcasts and flood the UI thread.
            if (added)
            {
                EmitPlaybackStateChanged();
            }
        }
    }

    public void EnqueueNext(Track track)
    {
        lock (_queueLock)
        {
            var item = new QueueItem
            {
                Id = Guid.NewGuid().ToString(),
                Track = track,
                IsPlaying = false
            };

            int activeInsertIndex = _currentIndex + 1;
            if (activeInsertIndex < 0 || activeInsertIndex > _activeQueue.Count)
            {
                activeInsertIndex = _activeQueue.Count;
            }
            _activeQueue.Insert(activeInsertIndex, item);

            int naturalInsertIndex = -1;
            if (_currentIndex >= 0 && _currentIndex < _activeQueue.Count)
            {
                var currentItem = _activeQueue[_currentIndex];
                naturalInsertIndex = _unshuffledQueue.FindIndex(i => i.Id == currentItem.Id);
            }

            if (naturalInsertIndex != -1)
            {
                _unshuffledQueue.Insert(naturalInsertIndex + 1, item);
            }
            else
            {
                _unshuffledQueue.Add(item);
            }

            EmitPlaybackStateChanged();
        }
    }

    public void PlayIndex(int index)
    {
        lock (_queueLock)
        {
            PlayIndexInternal(index);
        }
    }

    public void RemoveAt(int index)
    {
        lock (_queueLock)
        {
            if (index < 0 || index >= _activeQueue.Count)
                return;

            var itemToRemove = _activeQueue[index];

            if (index == _currentIndex)
            {
                _audioPlayer.Stop();
                itemToRemove.IsPlaying = false;
                _currentIndex = -1;
            }
            else if (_currentIndex > index)
            {
                _currentIndex--;
            }

            _activeQueue.RemoveAt(index);
            _unshuffledQueue.Remove(itemToRemove);

            EmitPlaybackStateChanged();
        }
    }

    public void Clear()
    {
        lock (_queueLock)
        {
            _audioPlayer.Stop();
            foreach (var item in _activeQueue)
            {
                item.IsPlaying = false;
            }

            _activeQueue.Clear();
            _unshuffledQueue.Clear();
            _currentIndex = -1;

            EmitPlaybackStateChanged();
        }
    }

    public void Reorder(int oldIndex, int newIndex)
    {
        lock (_queueLock)
        {
            if (oldIndex < 0 || oldIndex >= _activeQueue.Count || newIndex < 0 || newIndex >= _activeQueue.Count)
                return;

            var item = _activeQueue[oldIndex];
            _activeQueue.RemoveAt(oldIndex);
            _activeQueue.Insert(newIndex, item);

            if (_currentIndex == oldIndex)
            {
                _currentIndex = newIndex;
            }
            else if (oldIndex < _currentIndex && newIndex >= _currentIndex)
            {
                _currentIndex--;
            }
            else if (oldIndex > _currentIndex && newIndex <= _currentIndex)
            {
                _currentIndex++;
            }

            if (!_isShuffle)
            {
                _unshuffledQueue.Clear();
                _unshuffledQueue.AddRange(_activeQueue);
            }

            EmitPlaybackStateChanged();
        }
    }

    public void SetShuffle(bool enable)
    {
        lock (_queueLock)
        {
            if (_isShuffle == enable) return;
            _isShuffle = enable;

            if (_isShuffle)
            {
                QueueItem? currentItem = null;
                if (_currentIndex >= 0 && _currentIndex < _activeQueue.Count)
                {
                    currentItem = _activeQueue[_currentIndex];
                }

                var listToShuffle = new List<QueueItem>(_unshuffledQueue);
                if (currentItem != null)
                {
                    listToShuffle.Remove(currentItem);
                }

                var rng = new Random();
                int n = listToShuffle.Count;
                while (n > 1)
                {
                    n--;
                    int k = rng.Next(n + 1);
                    var value = listToShuffle[k];
                    listToShuffle[k] = listToShuffle[n];
                    listToShuffle[n] = value;
                }

                _activeQueue.Clear();
                if (currentItem != null)
                {
                    _activeQueue.Add(currentItem);
                    _currentIndex = 0;
                }
                else
                {
                    _currentIndex = -1;
                }
                _activeQueue.AddRange(listToShuffle);
            }
            else
            {
                QueueItem? currentItem = null;
                if (_currentIndex >= 0 && _currentIndex < _activeQueue.Count)
                {
                    currentItem = _activeQueue[_currentIndex];
                }

                _activeQueue.Clear();
                _activeQueue.AddRange(_unshuffledQueue);

                if (currentItem != null)
                {
                    _currentIndex = _activeQueue.FindIndex(item => item.Id == currentItem.Id);
                }
                else
                {
                    _currentIndex = -1;
                }
            }

            EmitPlaybackStateChanged();
        }
    }

    public void SetRepeatMode(RepeatMode mode)
    {
        lock (_queueLock)
        {
            _repeatMode = mode;
            EmitPlaybackStateChanged();
        }
    }

    public void PlayNext()
    {
        lock (_queueLock)
        {
            if (_activeQueue.Count == 0) return;

            int nextIndex = _currentIndex + 1;
            if (nextIndex >= _activeQueue.Count)
            {
                if (_repeatMode == RepeatMode.Queue)
                {
                    nextIndex = 0;
                }
                else
                {
                    _audioPlayer.Stop();
                    if (_currentIndex >= 0 && _currentIndex < _activeQueue.Count)
                    {
                        _activeQueue[_currentIndex].IsPlaying = false;
                    }
                    EmitPlaybackStateChanged();
                    return;
                }
            }

            PlayIndexInternal(nextIndex);
        }
    }

    public void PlayPrevious()
    {
        lock (_queueLock)
        {
            if (_activeQueue.Count == 0) return;

            double pos = _audioPlayer.GetPositionSeconds();
            if (pos > 3.0)
            {
                PlayIndexInternal(_currentIndex);
                return;
            }

            int prevIndex = _currentIndex - 1;
            if (prevIndex < 0)
            {
                if (_repeatMode == RepeatMode.Queue)
                {
                    prevIndex = _activeQueue.Count - 1;
                }
                else
                {
                    prevIndex = 0;
                }
            }

            PlayIndexInternal(prevIndex);
        }
    }

    public void Pause()
    {
        lock (_queueLock)
        {
            if (_audioPlayer.Status == PlaybackStatus.Playing)
            {
                _audioPlayer.Pause();
                EmitPlaybackStateChanged();
            }
        }
    }

    public void Resume()
    {
        lock (_queueLock)
        {
            if (_audioPlayer.Status == PlaybackStatus.Paused)
            {
                _audioPlayer.Resume();
                EmitPlaybackStateChanged();
            }
            else if (_audioPlayer.Status == PlaybackStatus.Stopped && _activeQueue.Count > 0)
            {
                int indexToPlay = _currentIndex >= 0 ? _currentIndex : 0;
                PlayIndexInternal(indexToPlay);
            }
        }
    }

    public void SetVolume(float volume)
    {
        lock (_queueLock)
        {
            _volume = Math.Clamp(volume, 0f, 1f);
            _audioPlayer.SetVolume(_volume);
            EmitPlaybackStateChanged();
        }
    }

    private void PlayIndexInternal(int index)
    {
        if (index < 0 || index >= _activeQueue.Count)
            return;

        if (_currentIndex >= 0 && _currentIndex < _activeQueue.Count)
        {
            _activeQueue[_currentIndex].IsPlaying = false;
        }

        var item = _activeQueue[index];
        var track = item.Track;

        bool isLocal = !track.SourceUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                       !track.SourceUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (isLocal && !System.IO.File.Exists(track.SourceUri))
        {
            System.Diagnostics.Debug.WriteLine($"[QueueService] Missing physical file detected (Storage may be offline): {track.SourceUri}");
            
            _activeQueue.RemoveAt(index);
            _unshuffledQueue.Remove(item);

            if (_activeQueue.Count == 0)
            {
                _audioPlayer.Stop();
                _currentIndex = -1;
                EmitPlaybackStateChanged();
                return;
            }

            if (index >= _activeQueue.Count)
            {
                if (_repeatMode == RepeatMode.Queue)
                {
                    index = 0;
                }
                else
                {
                    _audioPlayer.Stop();
                    _currentIndex = -1;
                    EmitPlaybackStateChanged();
                    return;
                }
            }

            PlayIndexInternal(index);
            return;
        }

        _currentIndex = index;
        _activeQueue[_currentIndex].IsPlaying = true;

        _audioPlayer.Play(track.SourceUri);

        EmitPlaybackStateChanged();
    }

    private async Task HandleTrackEndedAsync()
    {
        string? trackId = null;
        int expectedIndex = -1;

        lock (_queueLock)
        {
            if (_currentIndex >= 0 && _currentIndex < _activeQueue.Count)
            {
                trackId = _activeQueue[_currentIndex].Track.Id;
                expectedIndex = _currentIndex;
            }
        }

        if (trackId != null)
        {
            await _dbContext.LogPlaybackHistoryAsync(trackId);
        }

        lock (_queueLock)
        {
            // Epoch index stamp re-entrancy validation check
            if (_currentIndex != expectedIndex)
                return;

            if (_activeQueue.Count == 0 || _currentIndex < 0 || _currentIndex >= _activeQueue.Count)
                return;

            switch (_repeatMode)
            {
                case RepeatMode.Track:
                    PlayIndexInternal(_currentIndex);
                    break;
                case RepeatMode.None:
                    if (_currentIndex < _activeQueue.Count - 1)
                    {
                        PlayIndexInternal(_currentIndex + 1);
                    }
                    else
                    {
                        _audioPlayer.Stop();
                        _activeQueue[_currentIndex].IsPlaying = false;
                        EmitPlaybackStateChanged();
                    }
                    break;
                case RepeatMode.Queue:
                    PlayIndexInternal((_currentIndex + 1) % _activeQueue.Count);
                    break;
            }
        }
    }

    private PlaybackState GetCurrentState()
    {
        Track? currentTrack = null;
        if (_currentIndex >= 0 && _currentIndex < _activeQueue.Count)
        {
            currentTrack = _activeQueue[_currentIndex].Track;
        }

        return new PlaybackState(
            CurrentTrack: currentTrack,
            Status: _audioPlayer.Status,
            PositionSeconds: _audioPlayer.PositionSeconds,
            DurationSeconds: _audioPlayer.DurationSeconds,
            Volume: _volume,
            IsMuted: _isMuted,
            IsShuffle: _isShuffle,
            RepeatMode: _repeatMode,
            SequenceToken: _sequenceToken
        );
    }

    private void EmitPlaybackStateChanged()
    {
        lock (_queueLock)
        {
            _sequenceToken++;
            var state = GetCurrentState();
            CurrentState = state;
            PlaybackStateChanged?.Invoke(this, state);
        }
    }

    public PlaybackState Seek(double positionSeconds)
    {
        lock (_queueLock)
        {
            _audioPlayer.Seek(positionSeconds);
            _sequenceToken++;
            var state = GetCurrentState();
            CurrentState = state;
            PlaybackStateChanged?.Invoke(this, state);
            return state;
        }
    }
}
