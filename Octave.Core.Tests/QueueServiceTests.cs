using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Moq;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Audio;
using Octave.Core.Services.Database;
using Octave.Core.Services.Library;
using Octave.Core.Services.Playback;
using Xunit;

namespace Octave.Core.Tests;

public class QueueServiceTests : IDisposable
{
    // TEST-20: the fixture used to create a real temp file per test. A uniquely
    // NAMED shared-cache memory database replaces it — no disk I/O, no cleanup.
    // The keeper connection holds the named database alive between the context's
    // per-call open/close cycles, and the GUID name prevents two fixtures running
    // in parallel from ever attaching to the same physical store.
    private readonly SqliteConnection _keeper;
    private readonly SqliteDbContext _dbContext;
    private readonly Mock<IAudioPlayerService> _audioPlayerMock;
    private readonly Mock<ILibraryScanner> _scannerMock;

    public QueueServiceTests()
    {
        string connectionString = $"Data Source=octave_queue_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();

        _dbContext = new SqliteDbContext(connectionString);
        _dbContext.InitializeAsync().GetAwaiter().GetResult();

        _audioPlayerMock = new Mock<IAudioPlayerService>();
        _scannerMock = new Mock<ILibraryScanner>();

        _audioPlayerMock.Setup(a => a.Play(It.IsAny<string>(), It.IsAny<double>())).Returns(100L);
    }

    public void Dispose()
    {
        _keeper.Dispose(); // last reference gone — the named memory DB evaporates
        try { SqliteConnection.ClearPool(_keeper); } catch { }
    }

    [Fact]
    public async Task HandleTrackEndedAsync_ValidatesSessionId_IgnoresStaleEvents()
    {
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);

        var track1 = new Track("t1", "Track 1", "ar1", "Artist", "al1", "Album", 180, "http://test/1.mp3", "web", 1, 2024, DateTime.UtcNow);
        var track2 = new Track("t2", "Track 2", "ar1", "Artist", "al1", "Album", 180, "http://test/2.mp3", "web", 2, 2024, DateTime.UtcNow);

        await _dbContext.UpsertTrackAsync(track1);
        await _dbContext.UpsertTrackAsync(track2);

        queueService.EnqueueRange(new[] { track1, track2 });

        // Play track 1 (Session ID 100)
        queueService.PlayIndex(0);
        Assert.Equal("t1", queueService.CurrentState.CurrentTrack?.Id);

        // TEST-19: replace the old Task.Delay(100) sleeps with a deterministic
        // signal. The stale-session branch returns before its first await, so the
        // negative case needs no wait at all; the advance path re-enters Play,
        // which is our completion signal.
        var advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _audioPlayerMock.Setup(a => a.Play(It.IsAny<string>(), It.IsAny<double>()))
            .Returns(100L)
            .Callback(() => advanced.TrySetResult());

        // Raise TrackEnded with a stale Session ID (e.g. 50L) — rejected synchronously.
        _audioPlayerMock.Raise(a => a.TrackEnded += null, new TrackEndedEventArgs(50L, track1.SourceUri));

        // Queue should NOT advance because session ID did not match
        Assert.Equal("t1", queueService.CurrentState.CurrentTrack?.Id);

        // Raise TrackEnded with valid active Session ID (100L)
        _audioPlayerMock.Raise(a => a.TrackEnded += null, new TrackEndedEventArgs(100L, track1.SourceUri));
        await advanced.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Queue SHOULD advance to track 2
        Assert.Equal("t2", queueService.CurrentState.CurrentTrack?.Id);
    }

    [Fact]
    public void SetShuffle_WithSeededRandom_IsDeterministicAcrossInstances()
    {
        // TEST-08: shuffle used to be untestable because Random.Shared made the
        // permutation unrepeatable. With the injectable RNG the same seed must
        // yield the identical permutation — and a real (non-identity) one.
        var tracks = new List<Track>();
        for (int i = 0; i < 10; i++)
        {
            tracks.Add(new Track($"sd{i}", $"Track {i}", "ar1", "Artist", "al1", "Album", 180, $"http://test/{i}.mp3", "web", i, 2024, DateTime.UtcNow));
        }

        var first = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object, new Random(42));
        var second = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object, new Random(42));

        first.EnqueueRange(tracks);
        second.EnqueueRange(tracks);
        first.PlayIndex(0);
        second.PlayIndex(0);
        first.SetShuffle(true);
        second.SetShuffle(true);

        string[] order1 = first.GetCurrentQueue().Select(i => i.Track.Id).ToArray();
        string[] order2 = second.GetCurrentQueue().Select(i => i.Track.Id).ToArray();

        Assert.Equal(order1, order2);                                   // same seed ⇒ same permutation
        Assert.Equal(10, order1.Distinct().Count());                    // still a true permutation
        Assert.Equal(tracks.Select(t => t.Id).OrderBy(x => x), order1.OrderBy(x => x));
        Assert.NotEqual(tracks.Select(t => t.Id).ToArray(), order1);    // not the identity order
    }

    [Fact]
    public async Task RestorePositionOnStartup_Enabled_SeeksToSavedPositionOnce()
    {
        // TEST-11: companion to the default-off test — with the flag enabled, the
        // first play of the restored track must seek to exactly the saved position.
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);
        queueService.RestorePositionOnStartup = true;

        var track1 = new Track("t1", "Track 1", "ar1", "Artist", "al1", "Album", 180, "http://test/1.mp3", "web", 1, 2024, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track1);

        await _dbContext.SavePlayerStateAsync(new[] { "t1" }, new[] { "t1" }, 0, 90.0, 0.8f, false, RepeatMode.None);
        await queueService.RestoreAsync();
        queueService.PlayIndex(0);

        _audioPlayerMock.Verify(a => a.Seek(90.0), Times.Once);
    }

    [Fact]
    public void SetShuffle_PreservesOriginalUnshuffledQueueOrder()
    {
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);

        var tracks = new List<Track>();
        for (int i = 0; i < 10; i++)
        {
            tracks.Add(new Track($"id{i}", $"Track {i}", "ar1", "Artist", "al1", "Album", 180, $"http://test/{i}.mp3", "web", i, 2024, DateTime.UtcNow));
        }

        queueService.EnqueueRange(tracks);
        queueService.PlayIndex(0);

        // Enable Shuffle
        queueService.SetShuffle(true);
        Assert.True(queueService.CurrentState.IsShuffle);

        // Disable Shuffle - original order must be restored
        queueService.SetShuffle(false);
        Assert.False(queueService.CurrentState.IsShuffle);

        var restoredQueue = queueService.GetCurrentQueue();
        Assert.Equal(10, restoredQueue.Count);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal($"id{i}", restoredQueue[i].Track.Id);
        }
    }

    [Fact]
    public void ManagedBassAudioService_CrossfadeDurationMs_ClampsNegativeValues()
    {
        using var audioService = new ManagedBassAudioService();
        Assert.Equal(1000, audioService.CrossfadeDurationMs);

        audioService.CrossfadeDurationMs = 2500;
        Assert.Equal(2500, audioService.CrossfadeDurationMs);

        audioService.CrossfadeDurationMs = -500;
        Assert.Equal(0, audioService.CrossfadeDurationMs);
    }

    [Fact]
    public async Task RestorePositionOnStartup_DefaultsToFalse_AndResetsPositionToZero()
    {
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);
        Assert.False(queueService.RestorePositionOnStartup);

        var track1 = new Track("t1", "Track 1", "ar1", "Artist", "al1", "Album", 180, "http://test/1.mp3", "web", 1, 2024, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track1);

        // Save state with position = 90.0s
        await _dbContext.SavePlayerStateAsync(new[] { "t1" }, new[] { "t1" }, 0, 90.0, 0.8f, false, RepeatMode.None);

        // RestoreAsync with RestorePositionOnStartup = false (default)
        await queueService.RestoreAsync();

        // Play restored track
        queueService.PlayIndex(0);

        // Verify Seek was NOT called on audio player (position reset to 0:00)
        _audioPlayerMock.Verify(a => a.Seek(It.IsAny<double>()), Times.Never);
    }

    [Fact]
    public void EnqueueRange_NullOrEmpty_DoesNotThrow()
    {
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);
        queueService.EnqueueRange(null!);
        queueService.EnqueueRange(Array.Empty<Track>());
        Assert.Empty(queueService.GetCurrentQueue());
    }

    [Fact]
    public void Reorder_MovesTrackAndMaintainsSurrogateIds()
    {
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);
        var t1 = new Track("t1", "Track 1", "ar1", "Artist", "al1", "Album", 180, "http://test/1.mp3", "web", 1, 2024, DateTime.UtcNow);
        var t2 = new Track("t2", "Track 2", "ar1", "Artist", "al1", "Album", 180, "http://test/2.mp3", "web", 2, 2024, DateTime.UtcNow);
        var t3 = new Track("t3", "Track 3", "ar1", "Artist", "al1", "Album", 180, "http://test/3.mp3", "web", 3, 2024, DateTime.UtcNow);

        queueService.EnqueueRange(new[] { t1, t2, t3 });

        // Reorder index 0 to index 2
        queueService.Reorder(0, 2);

        var queue = queueService.GetCurrentQueue();
        Assert.Equal("t2", queue[0].Track.Id);
        Assert.Equal("t3", queue[1].Track.Id);
        Assert.Equal("t1", queue[2].Track.Id);
    }

    // =================================================================
    // Batch 4 — queue integrity & event storms (§12.4 / §15 Batch 4)
    // =================================================================

    private static string MissingLocalPath(string fileName) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "octave_missing_" + fileName + ".mp3");

    [Fact]
    public void SetShuffle_TrueWhilePlaying_CurrentTrackAppearsExactlyOnce()
    {
        // QUEUE-09: the active-list instance and its unshuffled twin are distinct
        // objects; the old reference-based Remove matched nothing and duplicated the
        // playing track (queue grew by one on every shuffle toggle).
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);

        var tracks = new List<Track>();
        for (int i = 0; i < 8; i++)
        {
            tracks.Add(new Track($"sq{i}", $"Track {i}", "ar1", "Artist", "al1", "Album", 180, $"http://test/{i}.mp3", "web", i, 2024, DateTime.UtcNow));
        }

        queueService.EnqueueRange(tracks);
        queueService.PlayIndex(3);
        queueService.SetShuffle(true);

        var shuffled = queueService.GetCurrentQueue();
        Assert.Equal(8, shuffled.Count); // was 9 with the duplication bug
        Assert.Equal(1, shuffled.Count(i => i.Track.Id == "sq3"));
        Assert.Equal("sq3", shuffled[0].Track.Id); // playing track pinned to front
    }

    [Fact]
    public void EnqueueNext_BeforeAnythingPlays_InsertsAtFrontOfNaturalOrderToo()
    {
        // QUEUE-10: with _currentIndex == -1 the item went to the front of the
        // active queue but was APPENDED to the unshuffled mirror — the two lists
        // disagreed and shuffle-off later surfaced the wrong order.
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);
        var t1 = new Track("t1", "Track 1", "ar1", "Artist", "al1", "Album", 180, "http://test/1.mp3", "web", 1, 2024, DateTime.UtcNow);
        var t2 = new Track("t2", "Track 2", "ar1", "Artist", "al1", "Album", 180, "http://test/2.mp3", "web", 2, 2024, DateTime.UtcNow);
        var tx = new Track("tx", "Next Up", "ar1", "Artist", "al1", "Album", 180, "http://test/x.mp3", "web", 3, 2024, DateTime.UtcNow);

        queueService.EnqueueRange(new[] { t1, t2 });
        queueService.EnqueueNext(tx); // nothing playing yet

        // Round-trip through shuffle to force the active queue to be rebuilt from
        // the unshuffled mirror — the only externally visible proof of sync.
        queueService.SetShuffle(true);
        queueService.SetShuffle(false);

        var queue = queueService.GetCurrentQueue();
        Assert.Equal(new[] { "tx", "t1", "t2" }, queue.Select(i => i.Track.Id).ToArray());
    }

    [Fact]
    public async Task RestoreAsync_SeedsUnshuffledQueue_FromPersistedUnshuffledOrder()
    {
        // QUEUE-08: closing while shuffled then restarting used to promote the
        // shuffled order to "original" — both queues were rebuilt from the saved
        // ACTIVE order even though UnshuffledTrackIds was persisted.
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);
        var t1 = new Track("t1", "Track 1", "ar1", "Artist", "al1", "Album", 180, "http://test/1.mp3", "web", 1, 2024, DateTime.UtcNow);
        var t2 = new Track("t2", "Track 2", "ar1", "Artist", "al1", "Album", 180, "http://test/2.mp3", "web", 2, 2024, DateTime.UtcNow);
        var t3 = new Track("t3", "Track 3", "ar1", "Artist", "al1", "Album", 180, "http://test/3.mp3", "web", 3, 2024, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(t1);
        await _dbContext.UpsertTrackAsync(t2);
        await _dbContext.UpsertTrackAsync(t3);

        // Persisted mid-session: shuffled active order, natural unshuffled order.
        await _dbContext.SavePlayerStateAsync(
            new[] { "t3", "t1", "t2" }, new[] { "t1", "t2", "t3" },
            0, 42.0, 0.7f, isShuffle: true, RepeatMode.None);

        await queueService.RestoreAsync();

        var restored = queueService.GetCurrentQueue();
        Assert.Equal(new[] { "t3", "t1", "t2" }, restored.Select(i => i.Track.Id).ToArray());
        Assert.True(queueService.CurrentState.IsShuffle);

        // Toggling shuffle off must recover the TRUE natural order.
        queueService.SetShuffle(false);
        var natural = queueService.GetCurrentQueue();
        Assert.Equal(new[] { "t1", "t2", "t3" }, natural.Select(i => i.Track.Id).ToArray());
    }

    [Fact]
    public async Task TrackEnded_UndecodableFilesForOneLap_StopsInsteadOfLoopingForever()
    {
        // QUEUE-02: files that exist but fail to decode fire TrackEnded at ~zero
        // position; with RepeatMode.Queue that used to loop the whole queue forever.
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);
        _audioPlayerMock.Setup(a => a.PositionSeconds).Returns(0.0); // never actually played

        // The files must EXIST (that's the undecodable case, not the missing case)
        // for the whole test — cleaned up in finally.
        var paths = new List<string>();
        try
        {
            var tracks = new List<Track>();
            for (int i = 0; i < 3; i++)
            {
                string path = MissingLocalPath("undecodable_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(path, "not audio"); // exists, but is not decodable
                paths.Add(path);
                tracks.Add(new Track($"ud{i}", $"Track {i}", "ar1", "Artist", "al1", "Album", 180, path, "Local", i, 2024, DateTime.UtcNow));
            }

            await _dbContext.UpsertTrackAsync(tracks[0]);
            await _dbContext.UpsertTrackAsync(tracks[1]);
            await _dbContext.UpsertTrackAsync(tracks[2]);

            // TEST-19: the old per-lap Task.Delay(50) sleeps are replaced by a
            // deterministic completion signal — the breaker's trip calls Stop().
            var breakerTripped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _audioPlayerMock.Setup(a => a.Stop()).Callback(() => breakerTripped.TrySetResult());

            queueService.EnqueueRange(tracks);
            queueService.PlayIndex(0);

            for (int i = 0; i < 3; i++) // one full lap of 3 undecodable tracks trips the breaker
            {
                _audioPlayerMock.Raise(a => a.TrackEnded += null, new TrackEndedEventArgs(100L, tracks[i].SourceUri));
            }

            await breakerTripped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _audioPlayerMock.Verify(a => a.Stop(), Times.Once);
            // Playback halted on the last lap entry — it did not wrap around and keep spinning.
            Assert.Equal("ud2", queueService.CurrentState.CurrentTrack?.Id);
        }
        finally
        {
            foreach (var p in paths)
            {
                try { File.Delete(p); } catch { }
            }
        }
    }

    [Fact]
    public void RemoveAt_CrossListRemoval_ShuffleOffDoesNotResurrectRemovedTrack()
    {
        // New finding (Batch 4): _unshuffledQueue.Remove(item) compared an
        // active-list instance against the unshuffled twin — never matched, so the
        // removed track survived in the natural order and came back on shuffle-off.
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);
        var t1 = new Track("t1", "Track 1", "ar1", "Artist", "al1", "Album", 180, "http://test/1.mp3", "web", 1, 2024, DateTime.UtcNow);
        var t2 = new Track("t2", "Track 2", "ar1", "Artist", "al1", "Album", 180, "http://test/2.mp3", "web", 2, 2024, DateTime.UtcNow);
        var t3 = new Track("t3", "Track 3", "ar1", "Artist", "al1", "Album", 180, "http://test/3.mp3", "web", 3, 2024, DateTime.UtcNow);

        queueService.EnqueueRange(new[] { t1, t2, t3 });
        queueService.PlayIndex(0);
        queueService.RemoveAt(2); // remove t3 while t1 plays

        queueService.SetShuffle(true);
        queueService.SetShuffle(false);

        var queue = queueService.GetCurrentQueue();
        Assert.Equal(new[] { "t1", "t2" }, queue.Select(i => i.Track.Id).ToArray());
    }

    [Fact]
    public async Task PlayIndex_EmitsStateAndQueueChangedOnce_TrackStartedNoLongerRebroadcasts()
    {
        // QUEUE-03: TrackStarted fired synchronously inside Play and the handler
        // emitted PlaybackStateChanged again — every transition reached the UI twice.
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);
        int stateChanges = 0;
        int queueChanges = 0;
        queueService.PlaybackStateChanged += (s, e) => stateChanges++;
        queueService.QueueChanged += (s, e) => queueChanges++;

        var t1 = new Track("t1", "Track 1", "ar1", "Artist", "al1", "Album", 180, "http://test/1.mp3", "web", 1, 2024, DateTime.UtcNow);
        queueService.EnqueueRange(new[] { t1 });

        stateChanges = 0;
        queueChanges = 0;
        queueService.PlayIndex(0);
        Assert.Equal(1, stateChanges);
        Assert.Equal(1, queueChanges);

        // The audio service's own TrackStarted (real engine raises it during Play)
        // must not produce a second broadcast.
        _audioPlayerMock.Raise(a => a.TrackStarted += null, "http://test/1.mp3");
        Assert.Equal(1, stateChanges);
        Assert.Equal(1, queueChanges);

        await Task.CompletedTask;
    }

    [Fact]
    public void PlayIndex_AllEntriesMissingFiles_SkipsIterativelyWithoutOverflow()
    {
        // QUEUE-06: the missing-file skip recursed per entry; a fully-offline queue
        // could overflow the call stack. Now iterative — 20k dead entries must clear.
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);

        var tracks = new List<Track>();
        for (int i = 0; i < 20000; i++)
        {
            tracks.Add(new Track($"gone{i}", $"Track {i}", "ar1", "Artist", "al1", "Album", 180, MissingLocalPath($"gone_{i}"), "Local", i, 2024, DateTime.UtcNow));
        }
        queueService.EnqueueRange(tracks);

        queueService.PlayIndex(0);

        Assert.Empty(queueService.GetCurrentQueue());
        _audioPlayerMock.Verify(a => a.Play(It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        _audioPlayerMock.Verify(a => a.Stop(), Times.Once);
    }

    [Fact]
    public async Task PlayQueueItem_ByItemId_PlaysResolvedEntry()
    {
        // NP-10: VM clicks now resolve the index under the queue lock by item Id.
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);
        var t1 = new Track("t1", "Track 1", "ar1", "Artist", "al1", "Album", 180, "http://test/1.mp3", "web", 1, 2024, DateTime.UtcNow);
        var t2 = new Track("t2", "Track 2", "ar1", "Artist", "al1", "Album", 180, "http://test/2.mp3", "web", 2, 2024, DateTime.UtcNow);
        queueService.EnqueueRange(new[] { t1, t2 });

        string secondItemId = queueService.GetCurrentQueue()[1].Id;
        queueService.PlayQueueItem(secondItemId);

        Assert.Equal("t2", queueService.CurrentState.CurrentTrack?.Id);
        _audioPlayerMock.Verify(a => a.Play(It.Is<string>(p => p.Contains("2.mp3")), It.IsAny<double>()), Times.Once);
        await Task.CompletedTask;
    }

    [Fact]
    public void RemoveById_RemovesEntry_AndPreservesCurrentTrack()
    {
        // UI-NP-02: the Now Playing panel removes rows by item Id (its visible
        // window is NOT the full-queue index). Removing a non-current entry must
        // leave the playing track and its IsPlaying flag untouched.
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);
        var t1 = new Track("t1", "Track 1", "ar1", "Artist", "al1", "Album", 180, "http://test/1.mp3", "web", 1, 2024, DateTime.UtcNow);
        var t2 = new Track("t2", "Track 2", "ar1", "Artist", "al1", "Album", 180, "http://test/2.mp3", "web", 2, 2024, DateTime.UtcNow);
        var t3 = new Track("t3", "Track 3", "ar1", "Artist", "al1", "Album", 180, "http://test/3.mp3", "web", 3, 2024, DateTime.UtcNow);
        queueService.EnqueueRange(new[] { t1, t2, t3 });

        string thirdItemId = queueService.GetCurrentQueue()[2].Id;
        queueService.PlayIndex(0);

        queueService.RemoveById(thirdItemId);

        var queue = queueService.GetCurrentQueue();
        Assert.Equal(2, queue.Count);
        Assert.DoesNotContain(queue, i => i.Track.Id == "t3");
        Assert.Equal("t1", queueService.CurrentState.CurrentTrack?.Id);
        Assert.True(queue[0].IsPlaying);
    }

    [Fact]
    public void RemoveById_RemovingCurrentTrack_StopsPlayback()
    {
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);
        var t1 = new Track("t1", "Track 1", "ar1", "Artist", "al1", "Album", 180, "http://test/1.mp3", "web", 1, 2024, DateTime.UtcNow);
        var t2 = new Track("t2", "Track 2", "ar1", "Artist", "al1", "Album", 180, "http://test/2.mp3", "web", 2, 2024, DateTime.UtcNow);
        queueService.EnqueueRange(new[] { t1, t2 });
        queueService.PlayIndex(0);

        string currentItemId = queueService.GetCurrentQueue()[0].Id;
        queueService.RemoveById(currentItemId);

        Assert.Null(queueService.CurrentState.CurrentTrack);
        _audioPlayerMock.Verify(a => a.Stop(), Times.Once);
    }

    [Fact]
    public void RemoveById_UnknownOrEmptyId_IsANoOp()
    {
        var queueService = new QueueService(_audioPlayerMock.Object, _dbContext, _scannerMock.Object);
        var t1 = new Track("t1", "Track 1", "ar1", "Artist", "al1", "Album", 180, "http://test/1.mp3", "web", 1, 2024, DateTime.UtcNow);
        queueService.EnqueueRange(new[] { t1 });

        queueService.RemoveById("does-not-exist");
        queueService.RemoveById("");

        Assert.Single(queueService.GetCurrentQueue());
        _audioPlayerMock.Verify(a => a.Stop(), Times.Never);
    }
}
