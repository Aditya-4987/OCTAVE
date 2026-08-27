using System;
using System.Collections.Generic;
using System.IO;
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
            SourceUri: "C:\\NonExistentPath\\test.mp3",
            TrackNumber: 1,
            Year: 2024,
            DateAdded: DateTime.UtcNow
        );
    }

    private static (string AudioPath, string LrcPath) CreateTempTrackWithLrc(string lrcContent)
    {
        string audioPath = Path.Combine(Path.GetTempPath(), $"octave_test_{Guid.NewGuid():N}.mp3");
        File.WriteAllText(audioPath, ""); // dummy audio file
        string lrcPath = Path.ChangeExtension(audioPath, ".lrc");
        File.WriteAllText(lrcPath, lrcContent);
        return (audioPath, lrcPath);
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

    // ---- Per-Format Resolution & LRCLIB Dual-Caching Tests -------------------

    [Fact]
    public async Task GetLyricsAsync_LocalSyncedOnly_DerivesPlainFromSyncedAndSkipsNetwork()
    {
        string localLrc = "[00:05.00] Local Synced Line";
        var (audioPath, lrcPath) = CreateTempTrackWithLrc(localLrc);

        try
        {
            var track = new Track(
                Id: "track_local_synced",
                Title: "Local Synced Track",
                ArtistId: "artist_1",
                ArtistName: "Artist",
                AlbumId: "album_1",
                AlbumTitle: "Album",
                DurationSeconds: 180,
                SourceUri: audioPath,
                TrackNumber: 1,
                Year: 2024,
                DateAdded: DateTime.UtcNow
            );

            var mockRepo = new Mock<ILyricsRepository>();
            var mockLrclib = new Mock<ILrclibClient>();

            mockRepo.Setup(r => r.GetCachedLyricsAsync("track_local_synced", It.IsAny<CancellationToken>()))
                .ReturnsAsync((CachedLyricsEntity?)null);

            // LYRIC-01: synced-only local lyrics are now self-complete — the plain view
            // is derived from the synced lines, so LRCLIB must never be consulted and the
            // every-play enrichment gap is gone.
            mockLrclib.Setup(l => l.GetLyricsAsync("Local Synced Track", "Artist", "Album", 180, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LrclibResponse(
                    Id: 3001,
                    TrackName: "Local Synced Track",
                    ArtistName: "Artist",
                    AlbumName: "Album",
                    Duration: 180,
                    Instrumental: false,
                    PlainLyrics: "Remote Plain Text",
                    SyncedLyrics: "[00:99.00] Remote Synced"));

            var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

            var result = await service.GetLyricsAsync(track);

            Assert.Equal(LyricsState.Synced, result.State);
            Assert.True(result.HasSyncedLyrics);
            Assert.True(result.HasPlainLyrics);
            Assert.Equal("Local Synced Line", result.PlainText); // derived from synced lines
            Assert.NotNull(result.SyncedLines);
            Assert.Single(result.SyncedLines);
            Assert.Equal("Local Synced Line", result.SyncedLines[0].Text); // Local synced preserved!

            mockLrclib.Verify(l => l.GetLyricsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
            mockRepo.Verify(r => r.UpsertCachedLyricsAsync(
                "track_local_synced",
                "Local Synced Line",
                localLrc,
                true,
                true,
                false,
                It.IsAny<string?>(),
                It.IsAny<long?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(audioPath)) File.Delete(audioPath);
            if (File.Exists(lrcPath)) File.Delete(lrcPath);
        }
    }

    [Fact]
    public async Task GetLyricsAsync_LocalStaticOnly_QueriesLrclibForMissingSynced_PreservesLocalStaticAndCachesBoth()
    {
        string localPlain = "Local Plain Text Line 1\nLocal Plain Text Line 2";
        var (audioPath, lrcPath) = CreateTempTrackWithLrc(localPlain);

        try
        {
            var track = new Track(
                Id: "track_local_static",
                Title: "Local Static Track",
                ArtistId: "artist_1",
                ArtistName: "Artist",
                AlbumId: "album_1",
                AlbumTitle: "Album",
                DurationSeconds: 180,
                SourceUri: audioPath,
                TrackNumber: 1,
                Year: 2024,
                DateAdded: DateTime.UtcNow
            );

            var mockRepo = new Mock<ILyricsRepository>();
            var mockLrclib = new Mock<ILrclibClient>();

            mockRepo.Setup(r => r.GetCachedLyricsAsync("track_local_static", It.IsAny<CancellationToken>()))
                .ReturnsAsync((CachedLyricsEntity?)null);

            var remoteResponse = new LrclibResponse(
                Id: 3002,
                TrackName: "Local Static Track",
                ArtistName: "Artist",
                AlbumName: "Album",
                Duration: 180,
                Instrumental: false,
                PlainLyrics: "Remote Plain (Must Not Overwrite Local)",
                SyncedLyrics: "[00:10.00] Remote Synced Line"
            );

            mockLrclib.Setup(l => l.GetLyricsAsync("Local Static Track", "Artist", "Album", 180, It.IsAny<CancellationToken>()))
                .ReturnsAsync(remoteResponse);

            var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

            var result = await service.GetLyricsAsync(track);

            Assert.Equal(LyricsState.Synced, result.State);
            Assert.True(result.HasSyncedLyrics);
            Assert.True(result.HasPlainLyrics);
            Assert.Contains("Local Plain Text Line 1", result.PlainText); // Local static preserved!
            Assert.NotNull(result.SyncedLines);
            Assert.Single(result.SyncedLines);
            Assert.Equal("Remote Synced Line", result.SyncedLines[0].Text);

            mockLrclib.Verify(l => l.GetLyricsAsync("Local Static Track", "Artist", "Album", 180, It.IsAny<CancellationToken>()), Times.Once);
            mockRepo.Verify(r => r.UpsertCachedLyricsAsync(
                "track_local_static",
                It.Is<string>(p => p.Contains("Local Plain Text Line 1")),
                "[00:10.00] Remote Synced Line",
                true,
                true,
                false,
                It.IsAny<string?>(),
                It.IsAny<long?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(audioPath)) File.Delete(audioPath);
            if (File.Exists(lrcPath)) File.Delete(lrcPath);
        }
    }

    [Fact]
    public async Task GetLyricsAsync_LocalSyncedAndStatic_DoesNotQueryLrclib()
    {
        string localBoth = "[00:05.00] Local Synced Line\n[00:10.00] Second Synced Line\nPlain text line here";
        var (audioPath, lrcPath) = CreateTempTrackWithLrc(localBoth);

        try
        {
            var track = new Track(
                Id: "track_local_both",
                Title: "Local Both Track",
                ArtistId: "artist_1",
                ArtistName: "Artist",
                AlbumId: "album_1",
                AlbumTitle: "Album",
                DurationSeconds: 180,
                SourceUri: audioPath,
                TrackNumber: 1,
                Year: 2024,
                DateAdded: DateTime.UtcNow
            );

            var mockRepo = new Mock<ILyricsRepository>();
            var mockLrclib = new Mock<ILrclibClient>();

            var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

            var result = await service.GetLyricsAsync(track);

            Assert.Equal(LyricsState.Synced, result.State);
            Assert.True(result.HasSyncedLyrics);
            Assert.True(result.HasPlainLyrics);

            // LRCLIB must NOT be queried when both formats exist locally
            mockLrclib.Verify(l => l.GetLyricsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (File.Exists(audioPath)) File.Delete(audioPath);
            if (File.Exists(lrcPath)) File.Delete(lrcPath);
        }
    }

    [Fact]
    public async Task GetLyricsAsync_DbSyncedOnly_DerivesPlainFromSyncedAndSkipsNetwork()
    {
        var track = CreateTestTrack("track_db_synced", "DB Synced Track", "Artist", "Album", 180);
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        var cached = new CachedLyricsEntity(
            TrackId: "track_db_synced",
            PlainLyrics: null,
            SyncedLyrics: "[00:08.00] DB Synced Line",
            HasPlainLyrics: false,
            HasSyncedLyrics: true,
            IsNotFound: false,
            CachedAt: DateTimeOffset.UtcNow.AddDays(-1),
            LastCheckedAt: DateTimeOffset.UtcNow.AddDays(-1),
            Source: "LRCLIB"
        );

        mockRepo.Setup(r => r.GetCachedLyricsAsync("track_db_synced", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached);

        // LYRIC-01: a cached synced-only row is now complete (plain derived from the
        // synced lines), so LRCLIB must never be consulted.
        mockLrclib.Setup(l => l.GetLyricsAsync("DB Synced Track", "Artist", "Album", 180, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LrclibResponse(
                Id: 4001,
                TrackName: "DB Synced Track",
                ArtistName: "Artist",
                AlbumName: "Album",
                Duration: 180,
                Instrumental: false,
                PlainLyrics: "Remote Plain Lyrics",
                SyncedLyrics: "[00:99.00] Remote Synced"));

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var result = await service.GetLyricsAsync(track);

        Assert.Equal(LyricsState.Synced, result.State);
        Assert.True(result.HasSyncedLyrics);
        Assert.True(result.HasPlainLyrics);
        Assert.Equal("DB Synced Line", result.PlainText); // derived from the cached synced lines
        Assert.NotNull(result.SyncedLines);
        Assert.Single(result.SyncedLines);
        Assert.Equal("DB Synced Line", result.SyncedLines[0].Text); // DB Synced preserved

        mockLrclib.Verify(l => l.GetLyricsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
        // Cache-only contribution is not re-upserted (no local file contributed).
        mockRepo.Verify(r => r.UpsertCachedLyricsAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetLyricsAsync_DbStaticOnly_QueriesLrclibForMissingSynced_PreservesDbStaticAndCachesBoth()
    {
        var track = CreateTestTrack("track_db_static", "DB Static Track", "Artist", "Album", 180);
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        var cached = new CachedLyricsEntity(
            TrackId: "track_db_static",
            PlainLyrics: "DB Plain Lyrics",
            SyncedLyrics: null,
            HasPlainLyrics: true,
            HasSyncedLyrics: false,
            IsNotFound: false,
            CachedAt: DateTimeOffset.UtcNow.AddDays(-1),
            LastCheckedAt: DateTimeOffset.UtcNow.AddDays(-1),
            Source: "LRCLIB"
        );

        mockRepo.Setup(r => r.GetCachedLyricsAsync("track_db_static", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached);

        var remoteResponse = new LrclibResponse(
            Id: 4002,
            TrackName: "DB Static Track",
            ArtistName: "Artist",
            AlbumName: "Album",
            Duration: 180,
            Instrumental: false,
            PlainLyrics: "Remote Plain (Do Not Overwrite)",
            SyncedLyrics: "[00:12.00] Remote Synced Line"
        );

        mockLrclib.Setup(l => l.GetLyricsAsync("DB Static Track", "Artist", "Album", 180, It.IsAny<CancellationToken>()))
            .ReturnsAsync(remoteResponse);

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var result = await service.GetLyricsAsync(track);

        Assert.Equal(LyricsState.Synced, result.State);
        Assert.True(result.HasSyncedLyrics);
        Assert.True(result.HasPlainLyrics);
        Assert.Equal("DB Plain Lyrics", result.PlainText); // DB Plain preserved
        Assert.NotNull(result.SyncedLines);
        Assert.Single(result.SyncedLines);
        Assert.Equal("Remote Synced Line", result.SyncedLines[0].Text);

        mockLrclib.Verify(l => l.GetLyricsAsync("DB Static Track", "Artist", "Album", 180, It.IsAny<CancellationToken>()), Times.Once);
        mockRepo.Verify(r => r.UpsertCachedLyricsAsync(
            "track_db_static",
            "DB Plain Lyrics",
            "[00:12.00] Remote Synced Line",
            true,
            true,
            false,
            It.IsAny<string?>(),
            It.IsAny<long?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetLyricsAsync_DbSyncedAndStatic_DoesNotQueryLrclib()
    {
        var track = CreateTestTrack("track_db_both");
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        var cached = new CachedLyricsEntity(
            TrackId: "track_db_both",
            PlainLyrics: "Cached Plain Lyrics",
            SyncedLyrics: "[00:10.00] Cached Synced Line",
            HasPlainLyrics: true,
            HasSyncedLyrics: true,
            IsNotFound: false,
            CachedAt: DateTimeOffset.UtcNow.AddDays(-1),
            LastCheckedAt: DateTimeOffset.UtcNow.AddDays(-1),
            Source: "LRCLIB"
        );

        mockRepo.Setup(r => r.GetCachedLyricsAsync("track_db_both", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached);

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var result = await service.GetLyricsAsync(track);

        Assert.Equal(LyricsState.Synced, result.State);
        Assert.True(result.HasSyncedLyrics);
        Assert.True(result.HasPlainLyrics);
        Assert.Equal("Cached Plain Lyrics", result.PlainText);
        Assert.Equal("LRCLIB", result.Source);
        Assert.NotNull(result.SyncedLines);
        Assert.Single(result.SyncedLines);

        // LRCLIB client must not be called when DB has both synced and static
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
        Assert.Equal("LRCLIB", result.Source);
        Assert.NotNull(result.SyncedLines);

        // Verify repository upsert was called with both forms and LRCLIB source
        mockRepo.Verify(r => r.UpsertCachedLyricsAsync(
            "track_remote",
            "Remote Plain Lyrics",
            "[00:05.00] Remote Synced Line",
            true,
            true,
            false,
            "LRCLIB",
            1001L,
            It.IsAny<string?>(),
            It.IsAny<string?>(),
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
            null,
            null,
            null,
            null,
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
            LastCheckedAt: DateTimeOffset.UtcNow.AddDays(-2),
            Source: null
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
            LastCheckedAt: DateTimeOffset.UtcNow.AddDays(-10),
            Source: null
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
        Assert.Equal("LRCLIB", result.Source);

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

        Assert.Equal(LyricsState.NetworkUnavailable, result.State);
        Assert.True(result.IsNetworkError);

        // Must NOT permanently cache negative result on temporary network error
        mockRepo.Verify(r => r.UpsertCachedLyricsAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            true, // isNotFound
            It.IsAny<string?>(),
            It.IsAny<long?>(),
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

    [Fact]
    public async Task GetLocalAndCachedLyricsAsync_CachedSyncedAndPlain_ReturnsImmediatelyWithoutNetwork()
    {
        var track = new Track("t1", "Cached Song", "ar1", "Cached Artist", "al1", "Album", 180, "http://test/song.mp3", 1, 2024, DateTime.UtcNow);
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        mockRepo.Setup(r => r.GetCachedLyricsAsync("t1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CachedLyricsEntity("t1", "Plain text from DB", "[00:01.00]Synced from DB", true, true, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "LRCLIB"));

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var result = await service.GetLocalAndCachedLyricsAsync(track);

        Assert.True(result.HasSyncedLyrics);
        Assert.True(result.HasPlainLyrics);
        Assert.Equal("Plain text from DB", result.PlainText);
        Assert.Equal(LyricsState.Synced, result.State);

        mockLrclib.Verify(l => l.GetLyricsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetLocalAndCachedLyricsAsync_CachedSyncedOnly_DerivesPlainTextWithoutCallingNetwork()
    {
        var track = new Track("t1", "Synced Only Song", "ar1", "Artist", "al1", "Album", 180, "http://test/song.mp3", 1, 2024, DateTime.UtcNow);
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        mockRepo.Setup(r => r.GetCachedLyricsAsync("t1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CachedLyricsEntity("t1", null, "[00:02.00]Only Synced Line", false, true, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Local file"));

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var fastResult = await service.GetLocalAndCachedLyricsAsync(track);

        Assert.True(fastResult.HasSyncedLyrics);
        // LYRIC-01: the plain view is derived from the synced lines, so the fast path
        // reports the track as complete and the enrichment gap never triggers a fetch.
        Assert.True(fastResult.HasPlainLyrics);
        Assert.Equal("Only Synced Line", fastResult.PlainText);
        Assert.Equal(LyricsState.Synced, fastResult.State);
        Assert.Single(fastResult.SyncedLines!);

        // Network must not have been touched during Phase 1
        mockLrclib.Verify(l => l.GetLyricsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnrichLyricsAsync_SyncedOnlyContentIsComplete_DoesNotQueryNetwork()
    {
        var track = new Track("t1", "Synced Only Song", "ar1", "Artist", "al1", "Album", 180, "http://test/song.mp3", 1, 2024, DateTime.UtcNow);
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        mockRepo.Setup(r => r.GetCachedLyricsAsync("t1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CachedLyricsEntity("t1", null, "[00:02.00]Original Synced Line", false, true, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Local file"));

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var fastResult = await service.GetLocalAndCachedLyricsAsync(track);
        Assert.True(fastResult.HasSyncedLyrics);
        Assert.True(fastResult.HasPlainLyrics); // derived — content is self-complete

        // Phase 2 enrichment short-circuits because both formats are resolved.
        var enrichedResult = await service.EnrichLyricsAsync(track, fastResult);

        Assert.True(enrichedResult.HasSyncedLyrics);
        Assert.True(enrichedResult.HasPlainLyrics);
        Assert.Equal("Original Synced Line", enrichedResult.PlainText);
        // Original local synced format must NOT be overwritten by remote synced
        Assert.Equal("[00:02.00]Original Synced Line", enrichedResult.RawSyncedLyrics);

        mockLrclib.Verify(l => l.GetLyricsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetLocalAndCachedLyricsAsync_SlowNetwork_DoesNotDelayLocalLookup()
    {
        var track = new Track("t1", "Fast Local Song", "ar1", "Artist", "al1", "Album", 180, "http://test/song.mp3", 1, 2024, DateTime.UtcNow);
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        // Mock a 10-second slow network response
        mockLrclib.Setup(l => l.GetLyricsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(10000);
                return null;
            });

        mockRepo.Setup(r => r.GetCachedLyricsAsync("t1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CachedLyricsEntity("t1", "Cached Text", null, true, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Local file"));

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.GetLocalAndCachedLyricsAsync(track);
        sw.Stop();

        // Must complete immediately in < 100ms
        Assert.True(sw.ElapsedMilliseconds < 100);
        Assert.True(result.HasPlainLyrics);
        Assert.Equal("Cached Text", result.PlainText);
    }

    [Fact]
    public async Task EnrichLyricsAsync_TwoStageLookup_DilliwaaliGirlfriend_FindsViaSearchAndCachesRecordId()
    {
        // Exact user scenario reproduction:
        // Local: Title has soundtrack attribution "(From ...)", artist order differs, album is compilation
        var track = new Track(
            "t-dilliwaali",
            "Dilliwaali Girlfriend (From \"Yeh Jawaani Hai Deewani\")",
            "ar1",
            "Arijit Singh; Sunidhi Chauhan; Pritam",
            "al1",
            "Sunidhi's Sassy Hits",
            260.0,
            "http://test/dilliwaali.mp3",
            1,
            2013,
            DateTime.UtcNow);

        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        // Stage 1: /api/get fails with 404/null for raw metadata
        mockLrclib.Setup(l => l.GetLyricsAsync(track.Title, track.ArtistName, track.AlbumTitle, track.DurationSeconds, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LrclibResponse?)null);

        // Stage 2: /api/search returns candidate list
        var candidate1 = new LrclibResponse(
            1001,
            "Dilliwaali Girlfriend",
            "Pritam, Arijit Singh, Sunidhi Chauhan",
            "Yeh Jawaani Hai Deewani (Original Motion Picture Soundtrack)",
            260.0,
            false,
            "Plain lyrics of Dilliwaali Girlfriend",
            "[00:15.00]Synced lyrics of Dilliwaali Girlfriend");

        var wrongCandidate = new LrclibResponse(
            1002,
            "Girlfriend",
            "Avril Lavigne",
            "The Best Damn Thing",
            217.0,
            false,
            "Hey hey you you",
            null);

        mockLrclib.Setup(l => l.SearchLyricsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LrclibResponse> { wrongCandidate, candidate1 });

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var fastResult = await service.GetLocalAndCachedLyricsAsync(track);
        var enriched = await service.EnrichLyricsAsync(track, fastResult);

        Assert.True(enriched.HasSyncedLyrics);
        Assert.True(enriched.HasPlainLyrics);
        Assert.Equal("Plain lyrics of Dilliwaali Girlfriend", enriched.PlainText);
        Assert.Equal(LyricsState.Synced, enriched.State);

        // Verify that matched LrclibRecordId (1001) was saved to the cache
        mockRepo.Verify(r => r.UpsertCachedLyricsAsync(
            "t-dilliwaali",
            "Plain lyrics of Dilliwaali Girlfriend",
            "[00:15.00]Synced lyrics of Dilliwaali Girlfriend",
            true,
            true,
            false,
            "LRCLIB",
            1001L,
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnrichLyricsAsync_UsesCachedLrclibRecordId_BypassesSearch()
    {
        var track = new Track(
            "t-cached-id",
            "Dilliwaali Girlfriend",
            "ar1",
            "Arijit Singh",
            "al1",
            "Album",
            260.0,
            "http://test/song.mp3",
            1,
            2013,
            DateTime.UtcNow);

        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        // Cache contains a previously matched LrclibRecordId (1001)
        mockRepo.Setup(r => r.GetCachedLyricsAsync("t-cached-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CachedLyricsEntity("t-cached-id", null, null, false, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, 1001L));

        mockLrclib.Setup(l => l.GetLyricsByIdAsync(1001L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LrclibResponse(1001L, "Dilliwaali Girlfriend", "Arijit Singh", "Album", 260.0, false, "Direct by ID lyrics", "[00:10.00]Direct synced"));

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        var fastResult = await service.GetLocalAndCachedLyricsAsync(track);
        var enriched = await service.EnrichLyricsAsync(track, fastResult);

        Assert.True(enriched.HasSyncedLyrics);
        Assert.Equal("Direct by ID lyrics", enriched.PlainText);

        // Direct fetch by ID used, search bypassed
        mockLrclib.Verify(l => l.GetLyricsByIdAsync(1001L, It.IsAny<CancellationToken>()), Times.Once);
        mockLrclib.Verify(l => l.SearchLyricsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReloadLyricsAsync_DeletesCache_ForcesRemoteQuery_ReplacesCacheRecord_PreservesLocalFile()
    {
        string localPlain = "Original Plain Lyrics in Local File";
        var (audioPath, lrcPath) = CreateTempTrackWithLrc(localPlain);

        try
        {
            var track = new Track(
                "t-reload",
                "Song To Reload",
                "ar1",
                "Artist",
                "al1",
                "Album",
                200.0,
                audioPath,
                1,
                2024,
                DateTime.UtcNow);

            var mockRepo = new Mock<ILyricsRepository>();
            var mockLrclib = new Mock<ILrclibClient>();

            var remoteResponse = new LrclibResponse(
                5555,
                "Song To Reload",
                "Artist",
                "Album",
                200.0,
                false,
                "Fresh Plain Lyrics from LRCLIB",
                "[00:10.00]Fresh Synced Lyrics from LRCLIB");

            mockLrclib.Setup(l => l.GetLyricsAsync("Song To Reload", "Artist", "Album", 200.0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(remoteResponse);

            var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

            var result = await service.ReloadLyricsAsync(track);

            // 1. Invalidation: DeleteCachedLyricsAsync was called
            mockRepo.Verify(r => r.DeleteCachedLyricsAsync("t-reload", It.IsAny<CancellationToken>()), Times.Once);

            // 2. Forced remote fetch
            mockLrclib.Verify(l => l.GetLyricsAsync("Song To Reload", "Artist", "Album", 200.0, It.IsAny<CancellationToken>()), Times.Once);

            // 3. New record saved in cache
            mockRepo.Verify(r => r.UpsertCachedLyricsAsync(
                "t-reload",
                "Fresh Plain Lyrics from LRCLIB",
                "[00:10.00]Fresh Synced Lyrics from LRCLIB",
                true,
                true,
                false,
                "LRCLIB",
                5555L,
                "LRCLIB",
                "LRCLIB",
                It.IsAny<CancellationToken>()), Times.Once);

            // 4. Returned fresh remote data
            Assert.True(result.HasSyncedLyrics);
            Assert.True(result.HasPlainLyrics);
            Assert.Equal("Fresh Plain Lyrics from LRCLIB", result.PlainText);
            Assert.Equal(LyricsState.Synced, result.State);

            // 5. Local audio file must be untouched
            Assert.True(File.Exists(audioPath));
            string currentLrc = File.ReadAllText(lrcPath);
            Assert.Equal(localPlain, currentLrc);
        }
        finally
        {
            if (File.Exists(audioPath)) File.Delete(audioPath);
            if (File.Exists(lrcPath)) File.Delete(lrcPath);
        }
    }

    [Fact]
    public async Task LyricsGenerationRace_Req1CompletesAfterReq2_OnlyReq2UpdatesActiveGeneration()
    {
        var trackA = CreateTestTrack("track_A", "Song A", "Artist A");
        var trackB = CreateTestTrack("track_B", "Song B", "Artist B");

        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        var tcsA = new TaskCompletionSource<LrclibResponse?>();
        var tcsB = new TaskCompletionSource<LrclibResponse?>();

        mockLrclib.Setup(l => l.GetLyricsAsync("Song A", "Artist A", It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .Returns(tcsA.Task);

        mockLrclib.Setup(l => l.GetLyricsAsync("Song B", "Artist B", It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .Returns(tcsB.Task);

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        long globalReq = 0;
        long activeReq = 0;
        LyricsData? appliedData = null;

        // User starts Track A (Req 1)
        long req1 = Interlocked.Increment(ref globalReq);
        activeReq = req1;
        var task1 = Task.Run(async () =>
        {
            var data1 = await service.GetLyricsAsync(trackA);
            if (req1 == activeReq)
            {
                appliedData = data1;
            }
        });

        // User rapidly switches to Track B (Req 2)
        long req2 = Interlocked.Increment(ref globalReq);
        activeReq = req2;
        var task2 = Task.Run(async () =>
        {
            var data2 = await service.GetLyricsAsync(trackB);
            if (req2 == activeReq)
            {
                appliedData = data2;
            }
        });

        // Req 2 (Track B) completes FIRST
        tcsB.SetResult(new LrclibResponse(2, "Song B", "Artist B", null, 200, false, "Lyrics for Track B", null));
        await task2;

        Assert.NotNull(appliedData);
        Assert.Equal("Lyrics for Track B", appliedData.PlainText);

        // Req 1 (Track A) completes AFTER Req 2
        tcsA.SetResult(new LrclibResponse(1, "Song A", "Artist A", null, 200, false, "Stale Lyrics for Track A", null));
        await task1;

        // Stale Req 1 must NOT have overwritten active Track B lyrics!
        Assert.NotNull(appliedData);
        Assert.Equal("Lyrics for Track B", appliedData.PlainText);
    }

    [Fact]
    public async Task GetLocalAndCachedLyricsAsync_L1CacheHit_ReturnsInstantlyWithoutCallingRepository()
    {
        var track = CreateTestTrack("track_l1_test");
        var mockRepo = new Mock<ILyricsRepository>();

        mockRepo.Setup(r => r.GetCachedLyricsAsync("track_l1_test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CachedLyricsEntity(
                TrackId: "track_l1_test",
                PlainLyrics: "Plain line 1\nPlain line 2",
                SyncedLyrics: "[00:01.00]Synced line 1\n[00:05.00]Synced line 2",
                HasPlainLyrics: true,
                HasSyncedLyrics: true,
                IsNotFound: false,
                CachedAt: DateTimeOffset.UtcNow,
                LastCheckedAt: DateTimeOffset.UtcNow,
                Source: "LRCLIB",
                LrclibRecordId: 12345,
                SyncedSource: "LRCLIB",
                StaticSource: "LRCLIB"
            ));

        var service = new LyricsService(null, mockRepo.Object);

        // First call populates L1 cache
        var firstResult = await service.GetLocalAndCachedLyricsAsync(track);
        Assert.True(firstResult.HasSyncedLyrics);
        Assert.True(firstResult.HasPlainLyrics);
        mockRepo.Verify(r => r.GetCachedLyricsAsync("track_l1_test", It.IsAny<CancellationToken>()), Times.Once);

        // Second call MUST hit L1 cache without calling repository again
        var secondResult = await service.GetLocalAndCachedLyricsAsync(track);
        Assert.Same(firstResult, secondResult);
        mockRepo.Verify(r => r.GetCachedLyricsAsync("track_l1_test", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetLocalAndCachedLyricsAsync_NegativeCacheActive_ReturnsUnavailableImmediately()
    {
        var track = CreateTestTrack("track_negative_test");
        var mockRepo = new Mock<ILyricsRepository>();

        mockRepo.Setup(r => r.GetCachedLyricsAsync("track_negative_test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CachedLyricsEntity(
                TrackId: "track_negative_test",
                PlainLyrics: null,
                SyncedLyrics: null,
                HasPlainLyrics: false,
                HasSyncedLyrics: false,
                IsNotFound: true,
                CachedAt: DateTimeOffset.UtcNow,
                LastCheckedAt: DateTimeOffset.UtcNow, // Fresh (< 7 days)
                Source: null,
                LrclibRecordId: null,
                SyncedSource: null,
                StaticSource: null
            ));

        var service = new LyricsService(null, mockRepo.Object);

        var result = await service.GetLocalAndCachedLyricsAsync(track);
        Assert.Equal(LyricsState.Unavailable, result.State);
        Assert.False(result.HasSyncedLyrics);
        Assert.False(result.HasPlainLyrics);
    }

    [Fact]
    public async Task ReloadLyricsAsync_InvalidatesL1Cache_AndFetchesRemote()
    {
        var track = CreateTestTrack("track_reload_l1");
        var mockRepo = new Mock<ILyricsRepository>();
        var mockLrclib = new Mock<ILrclibClient>();

        mockRepo.Setup(r => r.GetCachedLyricsAsync("track_reload_l1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CachedLyricsEntity(
                TrackId: "track_reload_l1",
                PlainLyrics: "Old Plain",
                SyncedLyrics: "[00:01.00]Old Synced",
                HasPlainLyrics: true,
                HasSyncedLyrics: true,
                IsNotFound: false,
                CachedAt: DateTimeOffset.UtcNow,
                LastCheckedAt: DateTimeOffset.UtcNow,
                Source: "LRCLIB",
                LrclibRecordId: 1,
                SyncedSource: "LRCLIB",
                StaticSource: "LRCLIB"
            ));

        mockLrclib.Setup(l => l.GetLyricsAsync(track.Title, track.ArtistName, track.AlbumTitle, track.DurationSeconds, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LrclibResponse(999, track.Title, track.ArtistName, track.AlbumTitle, track.DurationSeconds, false, "New Plain", "[00:01.00]New Synced"));

        var service = new LyricsService(mockLrclib.Object, mockRepo.Object);

        // Populate L1 cache
        var initial = await service.GetLocalAndCachedLyricsAsync(track);
        Assert.Equal("Old Plain", initial.PlainText);

        // Reload
        var reloaded = await service.ReloadLyricsAsync(track);
        Assert.Equal("New Plain", reloaded.PlainText);
        Assert.True(reloaded.HasSyncedLyrics);
        Assert.True(reloaded.HasPlainLyrics);

        // Ensure L1 cache now returns the new reloaded data
        var cachedAfterReload = await service.GetLocalAndCachedLyricsAsync(track);
        Assert.Equal("New Plain", cachedAfterReload.PlainText);
    }
}
