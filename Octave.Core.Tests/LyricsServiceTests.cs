using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Metadata;
using Xunit;

namespace Octave.Core.Tests;

public class LyricsServiceTests
{
    private static Track CreateTestTrack(string id = "track_1", string title = "Test Song", string artist = "Test Artist", string album = "Test Album", double duration = 200)
    {
        return new Track(
            Id: id,
            Title: title,
            ArtistId: "artist_1",
            ArtistName: artist,
            AlbumId: "album_1",
            AlbumTitle: album,
            DurationSeconds: duration,
            SourceUri: "C:\\Music\\test.mp3",
            TrackNumber: 1,
            Year: 2024,
            DateAdded: DateTime.UtcNow
        );
    }

    [Fact]
    public void ParseLrcContent_ValidTimestampedLrc_ReturnsSyncedLyricsWithOrderedLines()
    {
        string rawLrc = @"[ti:Test Song]
[ar:Octave Artist]
[00:04.50]First line of lyrics
[00:09.10]Second line of lyrics
[00:15.00]Third line of lyrics";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Synced, data.State);
        Assert.NotNull(data.SyncedLines);
        Assert.Equal(3, data.SyncedLines.Count);

        Assert.Equal(TimeSpan.FromSeconds(4.5), data.SyncedLines[0].Start);
        Assert.Equal("First line of lyrics", data.SyncedLines[0].Text);

        Assert.Equal(TimeSpan.FromSeconds(9.1), data.SyncedLines[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(9.1), data.SyncedLines[0].End);

        Assert.Equal(TimeSpan.FromSeconds(15.0), data.SyncedLines[2].Start);
        Assert.Null(data.SyncedLines[2].End);
    }

    [Fact]
    public void ParseLrcContent_PlainTextLrc_ReturnsUnsyncedLyrics()
    {
        string rawLrc = @"Line one of unsynced lyrics
Line two of unsynced lyrics
Line three of unsynced lyrics";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Unsynced, data.State);
        Assert.Null(data.SyncedLines);
        Assert.Contains("Line one", data.PlainText);
        Assert.Contains("Line three", data.PlainText);
    }

    [Fact]
    public void ParseLrcContent_EmptyOrInvalidString_ReturnsUnavailable()
    {
        var data = LyricsService.ParseLrcContent("track_1", "   ");
        Assert.Equal(LyricsState.Unavailable, data.State);
        Assert.Null(data.SyncedLines);
        Assert.Null(data.PlainText);
    }

    [Fact]
    public void LyricSynchronization_BoundaryCheck_And_BinarySearchSeek_WorkAccurately()
    {
        var lines = new List<LyricLine>
        {
            new LyricLine(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), "Line 1"),
            new LyricLine(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), "Line 2"),
            new LyricLine(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), "Line 3"),
            new LyricLine(TimeSpan.FromSeconds(30), null, "Line 4")
        };

        // Guards first: null list, empty list.
        Assert.Equal(-1, LyricsService.FindActiveLineIndex(null!, TimeSpan.FromSeconds(10)));
        Assert.Equal(-1, LyricsService.FindActiveLineIndex(new List<LyricLine>(), TimeSpan.FromSeconds(10)));

        // Exact boundaries land on the later line when timestamps coincide.
        Assert.Equal(-1, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(2.0)));  // Before start
        Assert.Equal(-1, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(4.99))); // Just before Line 1
        Assert.Equal(0, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(5.0)));   // Exactly Line 1 start
        Assert.Equal(0, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(5.5)));   // Line 1
        Assert.Equal(1, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(10.0)));  // Exactly Line 2 start
        Assert.Equal(1, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(12.0)));  // Line 2
        Assert.Equal(2, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(20.0)));  // Exactly Line 3 start
        Assert.Equal(2, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(25.0)));  // Line 3
        Assert.Equal(3, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(30.0)));  // Exactly Line 4 start
        Assert.Equal(3, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(45.0)));  // Line 4 (open-ended)

        // Unsorted input must not throw or loop forever — last matching Start wins.
        var shuffled = new List<LyricLine>(lines);
        (shuffled[1], shuffled[2]) = (shuffled[2], shuffled[1]);
        int idx = LyricsService.FindActiveLineIndex(shuffled, TimeSpan.FromSeconds(25.0));
        Assert.True(idx is 1 or 2, $"expected an active-line index in [1,2], got {idx}");
    }

    [Theory]
    [InlineData("00:04.5", 4.5)]      // single millisecond digit → ×100
    [InlineData("00:04.50", 4.5)]     // two digits → ×10
    [InlineData("00:04.500", 4.5)]    // three digits taken as-is
    [InlineData("01:04.5", 64.5)]     // minutes carry over
    public void ParseLrcContent_FractionalTimestampVariants_ParseToExactStarts(string stamp, double expectedSeconds)
    {
        string rawLrc = $"[{stamp}]Fractional line";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Synced, data.State);
        Assert.NotNull(data.SyncedLines);
        LyricLine line = Assert.Single(data.SyncedLines);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), line.Start);
    }

    [Fact]
    public void ParseLrcContent_MultiTimestampLine_CreatesOneEntryPerTimestamp()
    {
        string rawLrc = "[00:01.00][00:05.00]Shared chorus";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Synced, data.State);
        Assert.NotNull(data.SyncedLines);
        Assert.Equal(2, data.SyncedLines.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), data.SyncedLines[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(5), data.SyncedLines[1].Start);
        Assert.All(data.SyncedLines, l => Assert.Equal("Shared chorus", l.Text));
    }

    [Fact]
    public void ParseLrcContent_OffsetPushingBeforeZero_ClampsToZero()
    {
        string rawLrc = @"[offset:+6000]
[00:04.00]Clamped line
[00:09.00]Still positive line";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Synced, data.State);
        Assert.NotNull(data.SyncedLines);
        Assert.Equal(TimeSpan.Zero, data.SyncedLines[0].Start);          // 4000-6000 → clamped
        Assert.Equal(TimeSpan.FromSeconds(3), data.SyncedLines[1].Start); // 9000-6000 stays exact
    }

    [Fact]
    public void ParseLrcContent_WithPositiveOffset_ShiftsTimestampsEarlier()
    {
        string rawLrc = @"[offset:+500]
[00:05.00]First line
[00:10.00]Second line";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Synced, data.State);
        Assert.NotNull(data.SyncedLines);
        Assert.Equal(2, data.SyncedLines.Count);

        Assert.Equal(TimeSpan.FromSeconds(4.5), data.SyncedLines[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(9.5), data.SyncedLines[1].Start);
    }

    [Fact]
    public void ParseLrcContent_WithNegativeOffset_ShiftsTimestampsLater()
    {
        string rawLrc = @"[offset:-1000]
[00:05.00]First line
[00:10.00]Second line";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Synced, data.State);
        Assert.NotNull(data.SyncedLines);
        Assert.Equal(2, data.SyncedLines.Count);

        Assert.Equal(TimeSpan.FromSeconds(6.0), data.SyncedLines[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(11.0), data.SyncedLines[1].Start);
    }

    // ---- New LRCLIB & Dual-Caching Tests -------------------------------------

    [Fact]
    public async Task GetLyricsAsync_DbCacheHit_ReturnsCachedLyricsWithoutCallingLrclib()
    {
        var track = CreateTestTrack("track_cached");
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        var cached = new CachedLyricsEntity(
            TrackId: "track_cached",
            PlainLyrics: "Cached Plain Lyrics",
            SyncedLyrics: "[00:10.00] Cached Synced Line",
            HasPlainLyrics: true,
            HasSyncedLyrics: true,
            IsNotFound: false,
            CachedAt: DateTimeOffset.UtcNow.AddDays(-1),
            LastCheckedAt: DateTimeOffset.UtcNow.AddDays(-1)
        );

        mockRepo.Setup(r => r.GetCachedLyricsAsync("track_cached", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached);

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var result = await service.GetLyricsAsync(track);

        Assert.Equal(LyricsState.Synced, result.State);
        Assert.True(result.HasSyncedLyrics);
        Assert.True(result.HasPlainLyrics);
        Assert.Equal("Cached Plain Lyrics", result.PlainText);
        Assert.NotNull(result.SyncedLines);
        Assert.Single(result.SyncedLines);

        // LRCLIB client must not be called when DB cache hits
        mockLrclib.Verify(l => l.GetLyricsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetLyricsAsync_NotInCache_QueriesLrclib_CachesBothAndReturns()
    {
        var track = CreateTestTrack("track_remote", "Remote Song", "Remote Artist", "Remote Album", 180);
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        mockRepo.Setup(r => r.GetCachedLyricsAsync("track_remote", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CachedLyricsEntity?)null);

        var lrclibResponse = new LrclibResponse(
            Id: 1001,
            TrackName: "Remote Song",
            ArtistName: "Remote Artist",
            AlbumName: "Remote Album",
            Duration: 180,
            Instrumental: false,
            PlainLyrics: "Remote Plain Lyrics",
            SyncedLyrics: "[00:05.00] Remote Synced Line"
        );

        mockLrclib.Setup(l => l.GetLyricsAsync("Remote Song", "Remote Artist", "Remote Album", 180, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lrclibResponse);

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var result = await service.GetLyricsAsync(track);

        Assert.Equal(LyricsState.Synced, result.State);
        Assert.True(result.HasSyncedLyrics);
        Assert.True(result.HasPlainLyrics);
        Assert.Equal("Remote Plain Lyrics", result.PlainText);
        Assert.NotNull(result.SyncedLines);

        // Verify repository upsert was called with both forms
        mockRepo.Verify(r => r.UpsertCachedLyricsAsync(
            "track_remote",
            "Remote Plain Lyrics",
            "[00:05.00] Remote Synced Line",
            true,
            true,
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetLyricsAsync_LrclibReturns404_CachesAsNotFound_AndReturnsUnavailable()
    {
        var track = CreateTestTrack("track_404", "Missing Song", "Unknown Artist");
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        mockRepo.Setup(r => r.GetCachedLyricsAsync("track_404", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CachedLyricsEntity?)null);

        mockLrclib.Setup(l => l.GetLyricsAsync("Missing Song", "Unknown Artist", It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LrclibResponse?)null);

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var result = await service.GetLyricsAsync(track);

        Assert.Equal(LyricsState.Unavailable, result.State);
        Assert.False(result.HasSyncedLyrics);
        Assert.False(result.HasPlainLyrics);

        // Verify negative cache record saved
        mockRepo.Verify(r => r.UpsertCachedLyricsAsync(
            "track_404",
            null,
            null,
            false,
            false,
            true,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetLyricsAsync_FreshNegativeCacheEntry_ReturnsUnavailableWithoutCallingLrclib()
    {
        var track = CreateTestTrack("track_neg");
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        var cachedNotFound = new CachedLyricsEntity(
            TrackId: "track_neg",
            PlainLyrics: null,
            SyncedLyrics: null,
            HasPlainLyrics: false,
            HasSyncedLyrics: false,
            IsNotFound: true,
            CachedAt: DateTimeOffset.UtcNow.AddDays(-2),
            LastCheckedAt: DateTimeOffset.UtcNow.AddDays(-2) // fresh (< 7 days)
        );

        mockRepo.Setup(r => r.GetCachedLyricsAsync("track_neg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedNotFound);

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var result = await service.GetLyricsAsync(track);

        Assert.Equal(LyricsState.Unavailable, result.State);
        mockLrclib.Verify(l => l.GetLyricsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetLyricsAsync_ExpiredNegativeCacheEntry_QueriesLrclibAgain()
    {
        var track = CreateTestTrack("track_expired", "Previously Missing", "Artist");
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        var expiredNotFound = new CachedLyricsEntity(
            TrackId: "track_expired",
            PlainLyrics: null,
            SyncedLyrics: null,
            HasPlainLyrics: false,
            HasSyncedLyrics: false,
            IsNotFound: true,
            CachedAt: DateTimeOffset.UtcNow.AddDays(-14),
            LastCheckedAt: DateTimeOffset.UtcNow.AddDays(-10) // expired (> 7 days)
        );

        mockRepo.Setup(r => r.GetCachedLyricsAsync("track_expired", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expiredNotFound);

        var freshResponse = new LrclibResponse(
            Id: 2002,
            TrackName: "Previously Missing",
            ArtistName: "Artist",
            AlbumName: null,
            Duration: 200,
            Instrumental: false,
            PlainLyrics: "Newly added lyrics",
            SyncedLyrics: null
        );

        mockLrclib.Setup(l => l.GetLyricsAsync("Previously Missing", "Artist", It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(freshResponse);

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var result = await service.GetLyricsAsync(track);

        Assert.Equal(LyricsState.Unsynced, result.State);
        Assert.True(result.HasPlainLyrics);
        Assert.Equal("Newly added lyrics", result.PlainText);

        // LRCLIB was queried again because negative cache was expired
        mockLrclib.Verify(l => l.GetLyricsAsync("Previously Missing", "Artist", It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetLyricsAsync_LrclibThrowsNetworkError_ReturnsUnavailableWithIsNetworkError_DoesNotCacheNotFound()
    {
        var track = CreateTestTrack("track_net_err");
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        mockRepo.Setup(r => r.GetCachedLyricsAsync("track_net_err", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CachedLyricsEntity?)null);

        mockLrclib.Setup(l => l.GetLyricsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("No internet connection"));

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var result = await service.GetLyricsAsync(track);

        Assert.Equal(LyricsState.Unavailable, result.State);
        Assert.True(result.IsNetworkError);

        // Must NOT permanently cache negative result on temporary network error
        mockRepo.Verify(r => r.UpsertCachedLyricsAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            true, // isNotFound
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetLyricsAsync_CancellationAndRaceCondition_CancelsGracefully()
    {
        var trackA = CreateTestTrack("track_A", "Song A", "Artist A");
        var trackB = CreateTestTrack("track_B", "Song B", "Artist B");

        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        var ctsA = new CancellationTokenSource();

        // Track A simulates a slow remote fetch
        mockLrclib.Setup(l => l.GetLyricsAsync("Song A", "Artist A", It.IsAny<string>(), It.IsAny<double?>(), ctsA.Token))
            .Returns(async (string t, string a, string alb, double? d, CancellationToken ct) =>
            {
                await Task.Delay(500, ct);
                return new LrclibResponse(1, "Song A", "Artist A", null, 200, false, "Lyrics A", null);
            });

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var taskA = service.GetLyricsAsync(trackA, ctsA.Token);

        // User skips to Track B immediately
        ctsA.Cancel();

        // Track A must throw OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);

        // Track B executes with its own CTS
        using var ctsB = new CancellationTokenSource();
        mockLrclib.Setup(l => l.GetLyricsAsync("Song B", "Artist B", It.IsAny<string>(), It.IsAny<double?>(), ctsB.Token))
            .ReturnsAsync(new LrclibResponse(2, "Song B", "Artist B", null, 200, false, "Lyrics B", null));

        var resultB = await service.GetLyricsAsync(trackB, ctsB.Token);

        Assert.Equal("Lyrics B", resultB.PlainText);
        Assert.Equal(LyricsState.Unsynced, resultB.State);
    }
}
