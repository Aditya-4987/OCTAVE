using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
    private readonly string _dbPath;
    private readonly SqliteDbContext _dbContext;
    private readonly Mock<IAudioPlayerService> _audioPlayerMock;
    private readonly Mock<ILibraryScanner> _scannerMock;

    public QueueServiceTests()
    {
        _dbPath = Path.GetTempFileName() + ".db";
        _dbContext = new SqliteDbContext(_dbPath);
        _dbContext.InitializeAsync().GetAwaiter().GetResult();

        _audioPlayerMock = new Mock<IAudioPlayerService>();
        _scannerMock = new Mock<ILibraryScanner>();

        _audioPlayerMock.Setup(a => a.Play(It.IsAny<string>(), It.IsAny<double>())).Returns(100L);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch { }
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

        // Raise TrackEnded with a stale Session ID (e.g. 50L)
        _audioPlayerMock.Raise(a => a.TrackEnded += null, new TrackEndedEventArgs(50L, track1.SourceUri));
        await Task.Delay(100);

        // Queue should NOT advance because session ID did not match
        Assert.Equal("t1", queueService.CurrentState.CurrentTrack?.Id);

        // Raise TrackEnded with valid active Session ID (100L)
        _audioPlayerMock.Raise(a => a.TrackEnded += null, new TrackEndedEventArgs(100L, track1.SourceUri));
        await Task.Delay(100);

        // Queue SHOULD advance to track 2
        Assert.Equal("t2", queueService.CurrentState.CurrentTrack?.Id);
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
}
