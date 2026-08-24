using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Audio;
using Octave.Core.Services.Database;

namespace Octave.Core.Services.Playback;

public class QueueService : IQueueService, IDisposable
{
    public event EventHandler<PlaybackState>? PlaybackStateChanged;
    public event EventHandler? QueueChanged;
    public event EventHandler<double>? PositionChanged;

    public PlaybackState CurrentState { get; private set; }

    private readonly IAudioPlayerService _audioPlayer;
    private readonly SqliteDbContext _dbContext;
    private readonly ILibraryScanner _libraryScanner;

    // TEST-08: injectable so tests can seed the shuffle order deterministically.
    // Production default stays Random.Shared (QUEUE-07: one shared generator, no
    // per-call seed race).
    private readonly Random _shuffleRng;

    // Load-bearing lock-order invariant (§12.4): locking is always
    // _queueLock -> (audio service internals). The audio service releases its own
    // stream lock BEFORE firing TrackStarted/TrackEnded/PositionChanged, which is
    // the only reason this reverse direction is safe. Never raise an audio event
    // while holding a stream lock, and never block inside a queue-lock holder.
    private readonly object _queueLock = new();
    private readonly List<QueueItem> _unshuffledQueue = new();
    private readonly List<QueueItem> _activeQueue = new();

    private int _currentIndex = -1;
    private bool _isShuffle = false;
    private RepeatMode _repeatMode = RepeatMode.None;

    private long _sequenceToken = 0;

    // Startup-resume: when the queue is restored from disk we don't auto-play, but
    // remember where to seek so the first Play resumes at the saved position.
    private int _resumeIndex = -1;
    private double _resumePositionSeconds = 0;
    public bool RestorePositionOnStartup { get; set; } = false; // Default false (starts songs from beginning)
    private readonly System.Threading.SemaphoreSlim _persistenceSemaphore = new(1, 1);

    private long _activePlaybackSessionId = 0;
    private long _persistenceSequenceToken = 0;

    // QUEUE-01: one token allocator, but TWO watermarks. A lightweight progress/volume
    // write completing late must never advance past (and thereby drop) a pending
    // full-state save — queue order/shuffle/repeat/index used to be lost that way.
    // Full saves are only skipped by newer full saves; lightweight writes are skipped
    // by any newer completed save of either kind (so they can't clobber fresh data).
    private long _lastPersistedStateToken = 0;
    private long _lastPersistedProgressToken = 0;

    // QUEUE-05: progress ticks and volume drags share one throttled lightweight write.
    private const int LightweightSaveMinIntervalMs = 5000;
    private long _lastLightweightSaveTicks = 0;

    // QUEUE-02: consecutive auto-advances whose track produced ~no playback mean the
    // files exist but fail to decode. After one lap (bounded) of those, stop instead
    // of spinning the whole queue forever.
    private const double LoadFailurePositionThresholdSeconds = 0.75;
    private const int MaxConsecutiveLoadFailures = 10;
    private int _consecutiveLoadFailures = 0;

    private bool _disposed = false;

    // QUEUE-12: keep handler references so Dispose can detach them.
    private readonly EventHandler<TrackEndedEventArgs> _trackEndedHandler;
    private readonly EventHandler<double> _positionChangedHandler;
    private readonly EventHandler _libraryChangedHandler;

    public QueueService(
        IAudioPlayerService audioPlayer,
        SqliteDbContext dbContext,
        ILibraryScanner libraryScanner,
        Random? shuffleRng = null)
    {
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _libraryScanner = libraryScanner ?? throw new ArgumentNullException(nameof(libraryScanner));
        _shuffleRng = shuffleRng ?? Random.Shared;

        CurrentState = GetCurrentState();

        // Auto-advance loop subscription with required exception trapping and SessionId check.
        // (QUEUE-03: there is deliberately NO TrackStarted handler — Play fired it
        // synchronously inside PlayIndexInternal, and emitting PlaybackStateChanged from
        // here duplicated every state broadcast and rebuilt the UI twice per skip.)
        _trackEndedHandler = async (s, e) =>
        {
            try
            {
                await HandleTrackEndedAsync(e);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QueueService] Auto-advance failure: {ex}");
            }
        };
        _audioPlayer.TrackEnded += _trackEndedHandler;

        _libraryChangedHandler = (s, e) =>
        {
            PlaybackState? state = null;
            lock (_queueLock)
            {
                bool queueChanged = false;

                // Purge missing files from both queues
                for (int i = _activeQueue.Count - 1; i >= 0; i--)
                {
                    var track = _activeQueue[i].Track;
                    bool isLocal = !track.SourceUri.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                    if (isLocal && !System.IO.File.Exists(track.SourceUri))
                    {
                        if (_currentIndex == i)
                        {
                            _audioPlayer.Stop();
                            _currentIndex = -1;
                        }
                        else if (_currentIndex > i)
                        {
                            _currentIndex--;
                        }

                        _activeQueue.RemoveAt(i);
                        queueChanged = true;
                    }
                }

                for (int i = _unshuffledQueue.Count - 1; i >= 0; i--)
                {
                    var track = _unshuffledQueue[i].Track;
                    bool isLocal = !track.SourceUri.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                    if (isLocal && !System.IO.File.Exists(track.SourceUri))
                    {
                        _unshuffledQueue.RemoveAt(i);
                    }
                }

                if (queueChanged)
                {
                    state = CaptureStateUnlocked();
                }
            }
            if (state != null) RaisePlaybackEvents(state);
        };
        _libraryScanner.LibraryChanged += _libraryChangedHandler;

        // Forward the high-frequency position ticks as a lightweight event only.
        // Rebroadcasting the whole PlaybackState 4x/sec to every subscriber (and
        // marshaling each to the UI thread) was needless churn.
        _positionChangedHandler = (s, pos) =>
        {
            PositionChanged?.Invoke(this, pos);
            MaybeSaveLightweight(pos, _audioPlayer.Volume);
        };
        _audioPlayer.PositionChanged += _positionChangedHandler;
    }

    // ---- Persistence / startup resume -------------------------------------

    // Snapshots the current queue and writes it asynchronously. Caller MUST hold
    // _queueLock; the DB write itself runs off-thread.
    private void PersistStateUnlocked()
    {
        long token = Interlocked.Increment(ref _persistenceSequenceToken);
        var ids = new List<string>(_activeQueue.Count);
        foreach (var it in _activeQueue) ids.Add(it.Track.Id);
        var unshuffledIds = new List<string>(_unshuffledQueue.Count);
        foreach (var it in _unshuffledQueue) unshuffledIds.Add(it.Track.Id);

        int index = _currentIndex;
        double pos = _audioPlayer.PositionSeconds;
        float vol = _audioPlayer.Volume;
        bool shuffle = _isShuffle;
        RepeatMode repeat = _repeatMode;

        _ = Task.Run(async () =>
        {
            await _persistenceSemaphore.WaitAsync();
            try
            {
                if (token < Volatile.Read(ref _lastPersistedStateToken)) return;
                await _dbContext.SavePlayerStateAsync(ids, unshuffledIds, index, pos, vol, shuffle, repeat);
                _lastPersistedStateToken = token;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[QueueService] Persist failed: {ex.Message}"); }
            finally { _persistenceSemaphore.Release(); }
        });
    }

    // Throttled lightweight write (position + volume + mode flags, no queue rows)
    // shared by the position tick and volume changes (QUEUE-05).
    private void MaybeSaveLightweight(double pos, float vol)
    {
        long now = Environment.TickCount64;
        if (now - _lastLightweightSaveTicks < LightweightSaveMinIntervalMs) return;
        _lastLightweightSaveTicks = now;

        int index;
        bool shuffle;
        RepeatMode repeat;
        long token;
        lock (_queueLock)
        {
            index = _currentIndex;
            shuffle = _isShuffle;
            repeat = _repeatMode;
            token = Interlocked.Increment(ref _persistenceSequenceToken);
        }

        _ = Task.Run(async () =>
        {
            await _persistenceSemaphore.WaitAsync();
            try
            {
                // QUEUE-01: a lightweight write yields to ANY newer completed save, so
                // it can never overwrite fresher full-state data with stale index/flags.
                long newestCompleted = Math.Max(
                    Volatile.Read(ref _lastPersistedStateToken),
                    Volatile.Read(ref _lastPersistedProgressToken));
                if (token < newestCompleted) return;
                await _dbContext.UpdatePlaybackProgressAsync(index, pos, vol, shuffle, repeat);
                _lastPersistedProgressToken = token;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[QueueService] Progress save failed: {ex.Message}"); }
            finally { _persistenceSemaphore.Release(); }
        });
    }

    // Rebuilds the queue from the last persisted snapshot without auto-playing.
    // Non-blocking to call at startup; the UI updates via PlaybackStateChanged.
    public async Task RestoreAsync()
    {
        PersistedPlayerState? saved;
        try { saved = await _dbContext.LoadPlayerStateAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[QueueService] Restore load failed: {ex.Message}"); return; }
        if (saved == null || saved.TrackIds.Count == 0) return;

        string? currentTrackId = (saved.CurrentIndex >= 0 && saved.CurrentIndex < saved.TrackIds.Count)
            ? saved.TrackIds[saved.CurrentIndex]
            : null;

        List<Track> tracks;
        try
        {
            tracks = await _dbContext.GetTracksByIdsAsync(saved.TrackIds);
        }
        catch (Exception ex)
        {
            // INT-05: this call used to be the unguarded half of RestoreAsync — a
            // transient DB fault escaped as an unobserved-task exception and silently
            // skipped the restore.
            System.Diagnostics.Debug.WriteLine($"[QueueService] Restore tracks load failed: {ex.Message}");
            return;
        }
        if (tracks.Count == 0) return;

        // QUEUE-08: rebuild the TRUE natural order from the persisted unshuffled list.
        // Both queues used to be seeded from the saved ACTIVE order, so closing while
        // shuffled permanently promoted the shuffled order to "original".
        List<string> unshuffledIds = saved.UnshuffledTrackIds is { Count: > 0 }
            ? saved.UnshuffledTrackIds
            : saved.TrackIds; // pre-migration snapshots have no unshuffled rows

        List<Track> unshuffledTracks;
        try
        {
            unshuffledTracks = await _dbContext.GetTracksByIdsAsync(unshuffledIds);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QueueService] Restore unshuffled load failed: {ex.Message}");
            unshuffledTracks = tracks;
        }

        PlaybackState state;
        lock (_queueLock)
        {
            if (_activeQueue.Count > 0) return; // never clobber an in-progress session

            _activeQueue.Clear();
            foreach (var t in tracks)
            {
                _activeQueue.Add(new QueueItem { Id = Guid.NewGuid().ToString(), Track = t, IsPlaying = false });
            }

            _unshuffledQueue.Clear();
            foreach (var t in unshuffledTracks)
            {
                _unshuffledQueue.Add(new QueueItem { Id = Guid.NewGuid().ToString(), Track = t, IsPlaying = false });
            }

            _isShuffle = saved.IsShuffle;
            _repeatMode = saved.RepeatMode;

            int idx = 0;
            if (currentTrackId != null)
            {
                int found = _activeQueue.FindIndex(i => i.Track.Id == currentTrackId);
                if (found >= 0) idx = found;
            }
            _currentIndex = idx;
            _resumeIndex = idx;
            _resumePositionSeconds = RestorePositionOnStartup ? saved.PositionSeconds : 0;

            state = CaptureStateUnlocked();
        }

        try
        {
            _audioPlayer.Volume = saved.Volume;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QueueService] Restore volume apply failed: {ex.Message}");
        }

        // INT-05: a subscriber fault must not escape RestoreAsync as an unobserved
        // task exception at startup.
        try
        {
            RaisePlaybackEvents(state);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QueueService] Restore event dispatch failed: {ex.Message}");
        }
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
        PlaybackState state;
        lock (_queueLock)
        {
            string itemId = Guid.NewGuid().ToString();
            _unshuffledQueue.Add(new QueueItem { Id = itemId, Track = track, IsPlaying = false });
            _activeQueue.Add(new QueueItem { Id = itemId, Track = track, IsPlaying = false });

            state = CaptureStateUnlocked();
        }
        RaisePlaybackEvents(state);
    }

    public void EnqueueRange(IEnumerable<Track> tracks)
    {
        if (tracks == null) return;

        PlaybackState? state = null;
        lock (_queueLock)
        {
            bool added = false;
            foreach (var track in tracks)
            {
                string itemId = Guid.NewGuid().ToString();
                _unshuffledQueue.Add(new QueueItem { Id = itemId, Track = track, IsPlaying = false });
                _activeQueue.Add(new QueueItem { Id = itemId, Track = track, IsPlaying = false });
                added = true;
            }

            // Emit a single state change for the whole batch instead of one per
            // track - bulk-loading a large library used to fire thousands of
            // broadcasts and flood the UI thread.
            if (added)
            {
                state = CaptureStateUnlocked();
            }
        }
        if (state != null) RaisePlaybackEvents(state);
    }

    public void EnqueueNext(Track track)
    {
        PlaybackState state;
        lock (_queueLock)
        {
            string itemId = Guid.NewGuid().ToString();
            var activeItem = new QueueItem { Id = itemId, Track = track, IsPlaying = false };

            int activeInsertIndex = _currentIndex + 1;
            if (activeInsertIndex < 0 || activeInsertIndex > _activeQueue.Count)
            {
                activeInsertIndex = _activeQueue.Count;
            }
            _activeQueue.Insert(activeInsertIndex, activeItem);

            // QUEUE-10: when nothing is playing (_currentIndex == -1), "Play Next"
            // puts the item at the FRONT of the active queue; the unshuffled mirror
            // must agree or shuffle-off later resurrects a different order.
            int naturalInsertIndex = 0;
            if (_currentIndex >= 0 && _currentIndex < _activeQueue.Count)
            {
                var currentItem = _activeQueue[_currentIndex];
                int found = _unshuffledQueue.FindIndex(i => i.Id == currentItem.Id);
                naturalInsertIndex = found >= 0 ? found + 1 : _unshuffledQueue.Count;
            }
            _unshuffledQueue.Insert(Math.Clamp(naturalInsertIndex, 0, _unshuffledQueue.Count),
                new QueueItem { Id = itemId, Track = track, IsPlaying = false });

            state = CaptureStateUnlocked();
        }
        RaisePlaybackEvents(state);
    }

    public void PlayIndex(int index)
    {
        PlaybackState? state;
        lock (_queueLock)
        {
            state = PlayIndexInternal(index);
        }
        if (state != null) RaisePlaybackEvents(state);
    }

    public void PlayQueueItem(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return;

        PlaybackState? state;
        lock (_queueLock)
        {
            int index = _activeQueue.FindIndex(i => i.Id == itemId);
            if (index < 0) return;
            state = PlayIndexInternal(index);
        }
        if (state != null) RaisePlaybackEvents(state);
    }

    public void RemoveAt(int index)
    {
        PlaybackState? state;
        lock (_queueLock)
        {
            state = RemoveAtUnlocked(index);
        }
        if (state != null) RaisePlaybackEvents(state);
    }

    // IQueueService.RemoveById: resolves the entry by surrogate Id under the lock,
    // so a caller holding only an item reference (Now Playing panel rows) can't
    // act on a stale window index.
    public void RemoveById(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return;

        PlaybackState? state;
        lock (_queueLock)
        {
            int index = _activeQueue.FindIndex(i => i.Id == itemId);
            state = (index >= 0) ? RemoveAtUnlocked(index) : null;
        }
        if (state != null) RaisePlaybackEvents(state);
    }

    private PlaybackState? RemoveAtUnlocked(int index)
    {
        if (index < 0 || index >= _activeQueue.Count)
            return null;

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
        // Cross-list removals must match by Id: the two lists hold distinct
        // instances of the same queue entry (same root cause as QUEUE-09).
        _unshuffledQueue.RemoveAll(i => i.Id == itemToRemove.Id);

        return CaptureStateUnlocked();
    }

    public void Clear(bool keepCurrentTrack = false)
    {
        PlaybackState state;
        lock (_queueLock)
        {
            if (keepCurrentTrack && _currentIndex >= 0 && _currentIndex < _activeQueue.Count)
            {
                var currentItem = _activeQueue[_currentIndex];
                _activeQueue.Clear();
                _unshuffledQueue.Clear();
                _activeQueue.Add(new QueueItem { Id = currentItem.Id, Track = currentItem.Track, IsPlaying = currentItem.IsPlaying });
                _unshuffledQueue.Add(new QueueItem { Id = currentItem.Id, Track = currentItem.Track, IsPlaying = currentItem.IsPlaying });
                _currentIndex = 0;
            }
            else
            {
                _audioPlayer.Stop();
                foreach (var item in _activeQueue)
                {
                    item.IsPlaying = false;
                }

                _activeQueue.Clear();
                _unshuffledQueue.Clear();
                _currentIndex = -1;
            }

            state = CaptureStateUnlocked();
        }
        RaisePlaybackEvents(state);
    }

    public void Reorder(int oldIndex, int newIndex)
    {
        PlaybackState? state = null;
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
                foreach (var it in _activeQueue)
                {
                    _unshuffledQueue.Add(new QueueItem { Id = it.Id, Track = it.Track, IsPlaying = it.IsPlaying });
                }
            }
            // QUEUE-11 (intent confirmed): dragging within the shuffled view
            // rearranges play order ONLY — _unshuffledQueue stays frozen so toggling
            // shuffle off still restores the true source sequence. (It used to be
            // rewritten next to the dragged item's new active neighbor.)

            state = CaptureStateUnlocked();
        }
        if (state != null) RaisePlaybackEvents(state);
    }

    public void SetShuffle(bool enable)
    {
        PlaybackState? state;
        lock (_queueLock)
        {
            if (_isShuffle == enable) { state = null; }
            else
            {
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
                        // QUEUE-09: prune by Id. `Remove(currentItem)` compared the
                        // active-list instance against the unshuffled twin — distinct
                        // objects with equal Ids matched nothing, so the playing track
                        // was re-added at index 0 AND kept its shuffled copy.
                        listToShuffle.RemoveAll(i => i.Id == currentItem.Id);
                    }

                    // QUEUE-07/TEST-08: shared generator by default, injected seed in tests
                    Random rng = _shuffleRng;
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
                        _activeQueue.Add(new QueueItem { Id = currentItem.Id, Track = currentItem.Track, IsPlaying = currentItem.IsPlaying });
                        _currentIndex = 0;
                    }
                    else
                    {
                        _currentIndex = -1;
                    }

                    foreach (var it in listToShuffle)
                    {
                        _activeQueue.Add(new QueueItem { Id = it.Id, Track = it.Track, IsPlaying = false });
                    }
                }
                else
                {
                    QueueItem? currentItem = null;
                    if (_currentIndex >= 0 && _currentIndex < _activeQueue.Count)
                    {
                        currentItem = _activeQueue[_currentIndex];
                    }

                    _activeQueue.Clear();
                    foreach (var it in _unshuffledQueue)
                    {
                        _activeQueue.Add(new QueueItem { Id = it.Id, Track = it.Track, IsPlaying = (currentItem != null && it.Id == currentItem.Id) });
                    }

                    if (currentItem != null)
                    {
                        _currentIndex = _activeQueue.FindIndex(item => item.Id == currentItem.Id);
                    }
                    else
                    {
                        _currentIndex = -1;
                    }
                }

                state = CaptureStateUnlocked();
            }
        }
        if (state != null) RaisePlaybackEvents(state);
    }

    public void SetRepeatMode(RepeatMode mode)
    {
        PlaybackState state;
        lock (_queueLock)
        {
            _repeatMode = mode;
            state = CaptureStateUnlocked();
        }
        RaisePlaybackEvents(state);
    }

    public void PlayNext()
    {
        PlaybackState? state;
        lock (_queueLock)
        {
            state = PlayNextLocked();
        }
        if (state != null) RaisePlaybackEvents(state);
    }

    // Caller MUST hold _queueLock.
    private PlaybackState? PlayNextLocked()
    {
        if (_activeQueue.Count == 0) return null;

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
                return CaptureStateUnlocked();
            }
        }

        return PlayIndexInternal(nextIndex);
    }

    public void PlayPrevious()
    {
        PlaybackState? state;
        lock (_queueLock)
        {
            if (_activeQueue.Count == 0) { state = null; }
            else
            {
                double pos = _audioPlayer.GetPositionSeconds();
                if (pos > 3.0)
                {
                    state = PlayIndexInternal(_currentIndex);
                }
                else
                {
                    int prevIndex = _currentIndex - 1;
                    if (prevIndex < 0)
                    {
                        prevIndex = _repeatMode == RepeatMode.Queue
                            ? _activeQueue.Count - 1
                            : 0;
                    }
                    state = PlayIndexInternal(prevIndex);
                }
            }
        }
        if (state != null) RaisePlaybackEvents(state);
    }

    public void Pause()
    {
        PlaybackState? state = null;
        lock (_queueLock)
        {
            if (_audioPlayer.Status == PlaybackStatus.Playing)
            {
                _audioPlayer.Pause();
                state = CaptureStateUnlocked();
            }
        }
        if (state != null) RaisePlaybackEvents(state);
    }

    public void Resume()
    {
        PlaybackState? state;
        lock (_queueLock)
        {
            if (_audioPlayer.Status == PlaybackStatus.Paused)
            {
                _audioPlayer.Resume();
                state = CaptureStateUnlocked();
            }
            else if (_audioPlayer.Status == PlaybackStatus.Stopped && _activeQueue.Count > 0)
            {
                int indexToPlay = _currentIndex >= 0 ? _currentIndex : 0;
                state = PlayIndexInternal(indexToPlay);
            }
            else
            {
                state = null;
            }
        }
        if (state != null) RaisePlaybackEvents(state);
    }

    public void SetVolume(float volume)
    {
        PlaybackState state;
        lock (_queueLock)
        {
            _audioPlayer.Volume = volume;
            // QUEUE-05: volume drags used to persist a FULL snapshot per tick via the
            // emit below; update the visible state but let the throttled lightweight
            // write carry it to disk.
            state = CaptureStateUnlocked(persistSnapshot: false);
        }
        RaisePlaybackEvents(state);
        MaybeSaveLightweight(_audioPlayer.PositionSeconds, volume);
    }

    // Caller MUST hold _queueLock. Iteratively plays `index`, skipping over entries
    // whose local file has gone missing (QUEUE-06: this was tail recursion, so a
    // fully-offline queue could overflow the stack). Returns the fresh state for the
    // caller to raise AFTER releasing the lock, or null when there is nothing to raise.
    private PlaybackState? PlayIndexInternal(int index)
    {
        while (true)
        {
            if (index < 0 || index >= _activeQueue.Count)
                return null;

            var item = _activeQueue[index];
            var track = item.Track;

            bool isLocal = !track.SourceUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                           !track.SourceUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (isLocal && !System.IO.File.Exists(track.SourceUri))
            {
                System.Diagnostics.Debug.WriteLine($"[QueueService] Missing physical file detected (Storage may be offline): {track.SourceUri}");

                _activeQueue.RemoveAt(index);
                _unshuffledQueue.RemoveAll(i => i.Id == item.Id);

                if (_activeQueue.Count == 0)
                {
                    _audioPlayer.Stop();
                    _currentIndex = -1;
                    return CaptureStateUnlocked();
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
                        return CaptureStateUnlocked();
                    }
                }

                continue; // next entry has shifted into `index`
            }

            if (_currentIndex >= 0 && _currentIndex < _activeQueue.Count)
            {
                _activeQueue[_currentIndex].IsPlaying = false;
            }

            _currentIndex = index;
            _activeQueue[_currentIndex].IsPlaying = true;

            _activePlaybackSessionId = _audioPlayer.Play(track.SourceUri, track.ReplayGain);

            // One-time startup resume: seek to the saved position on the first play
            // of the restored track if RestorePositionOnStartup is enabled.
            if (RestorePositionOnStartup && index == _resumeIndex && _resumePositionSeconds > 0.5)
            {
                _audioPlayer.Seek(_resumePositionSeconds);
            }
            _resumeIndex = -1;
            _resumePositionSeconds = 0;

            return CaptureStateUnlocked();
        }
    }

    private async Task HandleTrackEndedAsync(TrackEndedEventArgs e)
    {
        if (e == null) return;

        string? trackId = null;
        int expectedIndex = -1;
        long expectedSessionId = e.SessionId;

        lock (_queueLock)
        {
            if (_activePlaybackSessionId != expectedSessionId)
                return;

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

        PlaybackState? state = null;
        lock (_queueLock)
        {
            // Session and Epoch index stamp validation check
            if (_activePlaybackSessionId != expectedSessionId || _currentIndex != expectedIndex)
                return;

            if (_activeQueue.Count == 0 || _currentIndex < 0 || _currentIndex >= _activeQueue.Count)
                return;

            // QUEUE-02: a track that ended at ~zero position never actually played —
            // the file exists but fails to decode. Count consecutive failures; after
            // one lap of the queue (bounded) stop instead of looping forever.
            double endedAtPosition = _audioPlayer.PositionSeconds;
            if (endedAtPosition >= LoadFailurePositionThresholdSeconds)
            {
                _consecutiveLoadFailures = 0;
            }
            else if (++_consecutiveLoadFailures >= Math.Min(_activeQueue.Count, MaxConsecutiveLoadFailures))
            {
                _consecutiveLoadFailures = 0;
                System.Diagnostics.Debug.WriteLine(
                    $"[QueueService] Every queued track failed to load ({_activeQueue.Count} attempted) — stopping playback.");
                _audioPlayer.Stop();
                _activeQueue[_currentIndex].IsPlaying = false;
                state = CaptureStateUnlocked();
            }
            else
            {
                switch (_repeatMode)
                {
                    case RepeatMode.Track:
                        state = PlayIndexInternal(_currentIndex);
                        break;
                    case RepeatMode.None:
                        if (_currentIndex < _activeQueue.Count - 1)
                        {
                            state = PlayIndexInternal(_currentIndex + 1);
                        }
                        else
                        {
                            _audioPlayer.Stop();
                            _activeQueue[_currentIndex].IsPlaying = false;
                            state = CaptureStateUnlocked();
                        }
                        break;
                    case RepeatMode.Queue:
                        state = PlayIndexInternal((_currentIndex + 1) % _activeQueue.Count);
                        break;
                }
            }
        }

        if (state != null) RaisePlaybackEvents(state);
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
            Volume: _audioPlayer.Volume,
            IsMuted: _audioPlayer.IsMuted,
            IsShuffle: _isShuffle,
            RepeatMode: _repeatMode,
            SequenceToken: _sequenceToken
        );
    }

    // Caller MUST hold _queueLock. Bumps the sequence token, snapshots state, and
    // (unless suppressed) queues a full-state persistence write. Raising the events
    // stays the caller's job AFTER releasing _queueLock (QUEUE-04: subscribers must
    // never run under the queue lock).
    private PlaybackState CaptureStateUnlocked(bool persistSnapshot = true)
    {
        _sequenceToken++;
        var state = GetCurrentState();
        CurrentState = state;
        if (persistSnapshot)
        {
            PersistStateUnlocked();
        }
        return state;
    }

    private void RaisePlaybackEvents(PlaybackState state)
    {
        PlaybackStateChanged?.Invoke(this, state);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public PlaybackState Seek(double positionSeconds)
    {
        PlaybackState state;
        lock (_queueLock)
        {
            _audioPlayer.Seek(positionSeconds);
            state = CaptureStateUnlocked();
        }
        PlaybackStateChanged?.Invoke(this, state);
        return state;
    }

    // QUEUE-12: detach the audio/scanner handlers so a non-singleton instance can be
    // collected and stops reacting to ended/position events.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audioPlayer.TrackEnded -= _trackEndedHandler;
        _audioPlayer.PositionChanged -= _positionChangedHandler;
        _libraryScanner.LibraryChanged -= _libraryChangedHandler;
    }
}
