using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Models;
using Octave.Core.Services.Database;
using Octave.Core.Services.Library;
using Octave.Core.Services.Metadata;
using Xunit;

namespace Octave.Core.Tests;

/// <summary>
/// Acceptance tests for the scanner batch (CODEBASE_AUDIT §15 Batch 9):
/// SCAN-08 unavailable-root zero deletions, SCAN-10 per-track skip,
/// SCAN-03/09 artist/album grouping (AC/DC + compilations), SCAN-11 disc
/// numbers + ordering, SCAN-04 case-variant reconciliation safety, SCAN-13
/// malformed-row tolerance, and SCAN-12 stable progress totals.
/// </summary>
public class LocalLibraryScannerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDbContext _dbContext;
    private readonly LocalLibraryScanner _scanner;

    private static readonly byte[] ValidMp3Bytes = new byte[] {
        // ID3v2.3 header (10 bytes: 'ID3', ver 3.0, flags 0, syncsafe size 10)
        0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A,
        // ID3 padding
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        // MPEG 1 Layer 3 sync word + frame header (128kbps 44.1kHz)
        0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    // Same proven-corrupt FLAC bytes used by TrackMetadataMatcherTests: real
    // "fLaC" magic followed by garbage — TagLib opens the container then throws.
    private static readonly byte[] UnreadableFlacBytes = { 0x66, 0x4C, 0x61, 0x43, 0xFF, 0x00, 0x00 };

    public LocalLibraryScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_ScanTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        string dbPath = Path.Combine(_tempDir, "scan.db");
        _dbContext = new SqliteDbContext(dbPath);
        _dbContext.InitializeAsync().GetAwaiter().GetResult();
        _scanner = new LocalLibraryScanner(_dbContext, new ArtworkCacheManager(_tempDir));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static Track MakeTrack(string id, string title, string sourceUri) =>
        new(id, title, "ar_" + id, "Artist " + id, "al_" + id, "Album", 180.0,
            sourceUri, "Local", 1, 2024, DateTime.UtcNow);

    /// <summary>Writes a minimal MP3 and stamps tags onto it via TagLib.</summary>
    private static void WriteTaggedFile(
        string path, string title, string performer, string album,
        int trackNumber, string? albumArtist = null, int disc = 1)
    {
        File.WriteAllBytes(path, ValidMp3Bytes);
        using var tagFile = TagLib.File.Create(path);
        tagFile.Tag.Title = title;
        tagFile.Tag.Performers = string.IsNullOrEmpty(performer) ? Array.Empty<string>() : new[] { performer };
        tagFile.Tag.AlbumArtists = string.IsNullOrEmpty(albumArtist) ? Array.Empty<string>() : new[] { albumArtist };
        tagFile.Tag.Album = album;
        tagFile.Tag.Track = (uint)trackNumber;
        tagFile.Tag.Disc = (uint)disc;
        tagFile.Save();
    }

    // =================================================================
    // SCAN-08: an unavailable or partially-discovered root deletes nothing.
    // =================================================================

    [Fact]
    public async Task ScanAsync_MissingRoot_DeletesZeroExistingTracks()
    {
        // A track whose file lives under a root that is currently unreachable
        // (USB unplugged / network share offline). The old code reconciled the
        // empty discovery against the DB and deleted it.
        string missingRoot = Path.Combine(_tempDir, "offline_root");
        await _dbContext.UpsertTrackAsync(MakeTrack("tr_ghost", "Ghost Track", Path.Combine(missingRoot, "ghost.mp3")));

        await _scanner.ScanAsync(missingRoot, CancellationToken.None);

        var all = await _dbContext.GetAllTracksAsync();
        Assert.Contains(all, t => t.Id == "tr_ghost");
    }

    [Fact]
    public async Task ScanAsync_ReachableButEmptyRoot_SkipsReconciliation()
    {
        // Second gate arm: an existing folder that yields ZERO files cannot be
        // distinguished from an un-enumerable one, so deletions are skipped too.
        string emptyRoot = Path.Combine(_tempDir, "empty_root");
        Directory.CreateDirectory(emptyRoot);
        await _dbContext.UpsertTrackAsync(MakeTrack("tr_stale", "Stale", Path.Combine(emptyRoot, "gone.mp3")));

        await _scanner.ScanAsync(emptyRoot, CancellationToken.None);

        var all = await _dbContext.GetAllTracksAsync();
        Assert.Contains(all, t => t.Id == "tr_stale");
    }

    // =================================================================
    // SCAN-10: one bad file never aborts the scan.
    // =================================================================

    [Fact]
    public async Task ScanAsync_CorruptFileAmongValid_IsSkippedWithoutAborting()
    {
        string root = Path.Combine(_tempDir, "corrupt_root");
        Directory.CreateDirectory(root);
        WriteTaggedFile(Path.Combine(root, "good.mp3"), "Real Song", "Real Artist", "Real Album", 1);
        File.WriteAllBytes(Path.Combine(root, "garbage.flac"), UnreadableFlacBytes);

        await _scanner.ScanAsync(root, CancellationToken.None);

        var all = await _dbContext.GetAllTracksAsync();
        Assert.Contains(all, t => t.Title == "Real Song");
        Assert.DoesNotContain(all, t => t.SourceUri.EndsWith("garbage.flac", StringComparison.OrdinalIgnoreCase));
    }

    // =================================================================
    // SCAN-03/SCAN-09: artist parsing and album keying.
    // =================================================================

    [Fact]
    public void ParseArtistNames_SlashBearingName_IsNotSplit()
    {
        string path = Path.Combine(_tempDir, "slash.mp3");
        WriteTaggedFile(path, "TNT", "", "Some Album", 1, albumArtist: null);
        using var tagFile = TagLib.File.Create(path);
        tagFile.Tag.Performers = new[] { "AC/DC" };
        tagFile.Save();

        var (individuals, display, primary, albumArtist) = LocalLibraryScanner.ParseArtistNames(tagFile);

        var individual = Assert.Single(individuals);
        Assert.Equal("AC/DC", individual);
        Assert.Equal("AC/DC", primary);
        Assert.Equal("AC/DC", display);
        Assert.Equal("AC/DC", albumArtist);
    }

    [Fact]
    public async Task ScanAsync_CompilationTracks_GroupUnderSingleAlbum()
    {
        // Different track performers, shared "Various Artists" album-artist tag:
        // the compilation must stay ONE album keyed on the album artist.
        string root = Path.Combine(_tempDir, "comp_root");
        Directory.CreateDirectory(root);
        WriteTaggedFile(Path.Combine(root, "01.mp3"), "Song One", "Alpha", "Indie Comp", 1, albumArtist: "Various Artists");
        WriteTaggedFile(Path.Combine(root, "02.mp3"), "Song Two", "Beta", "Indie Comp", 2, albumArtist: "Various Artists");

        await _scanner.ScanAsync(root, CancellationToken.None);

        string albumId = IdGenerator.FromAlbum("Various Artists", "Indie Comp");
        var tracks = await _dbContext.GetTracksByAlbumAsync(albumId);
        Assert.Equal(2, tracks.Count);
        // Track-level display keeps the performer (plus the album-artist credit
        // ParseArtistNames appends); the ALBUM is what must be single-keyed.
        Assert.Contains(tracks, t => t.Title == "Song One" && t.ArtistName.Contains("Alpha"));
        Assert.Contains(tracks, t => t.Title == "Song Two" && t.ArtistName.Contains("Beta"));

        var albums = await _dbContext.GetAllAlbumsAsync();
        var comp = Assert.Single(albums, a => a.Id == albumId);
        Assert.Equal("Various Artists", comp.ArtistName);
    }

    [Fact]
    public async Task ScanAsync_GuestFeatureTrack_StaysInParentArtistsAlbum()
    {
        // The guest's performer credit must not split the host artist's album.
        string root = Path.Combine(_tempDir, "feat_root");
        Directory.CreateDirectory(root);
        WriteTaggedFile(Path.Combine(root, "a.mp3"), "Host Song", "Main Artist", "Shared LP", 1);
        WriteTaggedFile(Path.Combine(root, "b.mp3"), "Guest Song", "Guest", "Shared LP", 7, albumArtist: "Main Artist");

        await _scanner.ScanAsync(root, CancellationToken.None);

        string albumId = IdGenerator.FromAlbum("Main Artist", "Shared LP");
        var tracks = await _dbContext.GetTracksByAlbumAsync(albumId);
        Assert.Equal(2, tracks.Count);
    }

    // =================================================================
    // SCAN-11: disc number persistence + disc-aware album ordering.
    // =================================================================

    [Fact]
    public async Task ScanAsync_MultiDiscFiles_PersistDiscAndOrderByDiscThenTrack()
    {
        string root = Path.Combine(_tempDir, "disc_root");
        Directory.CreateDirectory(root);
        WriteTaggedFile(Path.Combine(root, "d1t09.mp3"), "Late On Disc One", "Disc Artist", "Box Set", 9, disc: 1);
        WriteTaggedFile(Path.Combine(root, "d2t01.mp3"), "Opens Disc Two", "Disc Artist", "Box Set", 1, disc: 2);

        await _scanner.ScanAsync(root, CancellationToken.None);

        string albumId = IdGenerator.FromAlbum("Disc Artist", "Box Set");
        var ordered = await _dbContext.GetTracksByAlbumAsync(albumId);
        Assert.Equal(2, ordered.Count);

        var first = Assert.Single(ordered, t => t.Title == "Late On Disc One");
        Assert.Equal(1, first.DiscNumber);
        var second = Assert.Single(ordered, t => t.Title == "Opens Disc Two");
        Assert.Equal(2, second.DiscNumber);

        // Disc ordering wins over track number: disc 1 trk 9 precedes disc 2 trk 1.
        Assert.True(ordered.IndexOf(first) < ordered.IndexOf(second));
    }

    // =================================================================
    // SCAN-04/SCAN-13: reconciliation compares normalized paths and
    // tolerates malformed stored rows.
    // =================================================================

    [Fact]
    public async Task ScanAsync_CaseVariantStoredPath_IsNotDeleted()
    {
        // Old code compared the RAW stored uri against the RAW enumerated set but
        // gated on a normalized prefix — a casing difference wrongly deleted the
        // present file. Both sides are normalized now.
        string root = Path.Combine(_tempDir, "case_root");
        Directory.CreateDirectory(root);
        string actualPath = Path.Combine(root, "present.mp3");
        WriteTaggedFile(actualPath, "Present Song", "Case Artist", "Case Album", 1);

        string upperVariant = Path.Combine(_tempDir.ToUpperInvariant(),
            "CASE_ROOT", "PRESENT.MP3");
        Assert.NotEqual(actualPath, upperVariant); // sanity: the rows really differ textually
        await _dbContext.UpsertTrackAsync(MakeTrack("tr_casey", "Casey", upperVariant));

        await _scanner.ScanAsync(root, CancellationToken.None);

        var all = await _dbContext.GetAllTracksAsync();
        Assert.Contains(all, t => t.Id == "tr_casey");          // not deleted despite raw mismatch
        Assert.Contains(all, t => t.Title == "Present Song");   // scan itself worked
    }

    [Fact]
    public async Task ScanAsync_MalformedStoredPath_IsSkippedWithoutRollingBack()
    {
        // SCAN-13: one row with characters GetFullPath rejects must not abort the
        // whole reconciliation every run.
        string root = Path.Combine(_tempDir, "malformed_root");
        Directory.CreateDirectory(root);
        WriteTaggedFile(Path.Combine(root, "fine.mp3"), "Fine Song", "Malformed Artist", "Malformed Album", 1);

        string malformedUnderRoot = Path.Combine(root, "bad") + "\0broken"; // NUL is rejected by Path.GetFullPath
        Assert.ThrowsAny<ArgumentException>(() => Path.GetFullPath(malformedUnderRoot));
        await _dbContext.UpsertTrackAsync(MakeTrack("tr_badpath", "Bad Row", malformedUnderRoot));

        await _scanner.ScanAsync(root, CancellationToken.None); // must not throw

        var all = await _dbContext.GetAllTracksAsync();
        Assert.Contains(all, t => t.Id == "tr_badpath");     // skipped, not deleted
        Assert.Contains(all, t => t.Title == "Fine Song");
    }

    // =================================================================
    // SCAN-12: progress totals stabilize once enumeration completes.
    // =================================================================

    [Fact]
    public async Task ScanAsync_FinalProgressReport_CarriesStableTotal()
    {
        var events = new List<LibraryScanProgressEventArgs>();
        _scanner.ScanProgressChanged += (_, args) => events.Add(args);

        string root = Path.Combine(_tempDir, "progress_root");
        Directory.CreateDirectory(root);
        WriteTaggedFile(Path.Combine(root, "only.mp3"), "Only Song", "Progress Artist", "Progress Album", 1);

        await _scanner.ScanAsync(root, CancellationToken.None);

        var lastEvent = events[^1];
        Assert.Equal(1, lastEvent.TotalFilesFound); // a real total, never -1 at completion
        Assert.True(lastEvent.FilesProcessed >= 1);
        Assert.All(events.Take(events.Count - 1), e => Assert.True(e.TotalFilesFound >= -1));
    }

    // =================================================================
    // TEST-13: zero-byte inputs and cancellation mid-walk.
    // =================================================================

    [Fact]
    public async Task ScanAsync_ZeroByteFileAmongValid_IsSkippedAndValidStillIngested()
    {
        // A zero-length file (crashed download / interrupted sync) must be
        // rejected like any other undecodable input — without aborting the walk.
        string root = Path.Combine(_tempDir, "zerobyte_root");
        Directory.CreateDirectory(root);
        WriteTaggedFile(Path.Combine(root, "real.mp3"), "Real Song", "Real Artist", "Real Album", 1);
        using (var empty = File.Create(Path.Combine(root, "truncated.mp3"))) { }

        await _scanner.ScanAsync(root, CancellationToken.None);

        var all = await _dbContext.GetAllTracksAsync();
        Assert.Contains(all, t => t.Title == "Real Song");
        Assert.DoesNotContain(all, t => t.SourceUri.EndsWith("truncated.mp3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_PreCancelledToken_ThrowsWithoutDeletingExistingTracks()
    {
        string root = Path.Combine(_tempDir, "precancel_root");
        Directory.CreateDirectory(root);
        WriteTaggedFile(Path.Combine(root, "present.mp3"), "Present Song", "Precancel Artist", "Precancel Album", 1);
        await _dbContext.UpsertTrackAsync(MakeTrack("tr_precancel_ghost", "Precancel Ghost", Path.Combine(root, "ghost.mp3")));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The semaphore acquire observes the token before anything is walked,
        // written or reconciled — the call must surface the cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _scanner.ScanAsync(root, cts.Token));

        var all = await _dbContext.GetAllTracksAsync();
        Assert.Contains(all, t => t.Id == "tr_precancel_ghost");
    }

    [Fact]
    public async Task ScanAsync_CancelledMidScan_StopsWithOperationCanceled_AndDeletesNothing()
    {
        string root = Path.Combine(_tempDir, "midcancel_root");
        Directory.CreateDirectory(root);

        // 30 tagged files guarantees at least one progress event (every 25th)
        // fires while files remain unprocessed, making the mid-scan landing
        // deterministic rather than timing-dependent.
        const int fileCount = 30;
        for (int i = 0; i < fileCount; i++)
        {
            WriteTaggedFile(Path.Combine(root, $"song_{i:D2}.mp3"), $"Song {i}", "MidCancel Artist", "MidCancel Album", i + 1);
        }

        // A stale row whose physical file no longer exists: only reconciliation
        // can delete it, and a cancelled scan must never reach reconciliation.
        await _dbContext.UpsertTrackAsync(MakeTrack("tr_midghost", "MidGhost", Path.Combine(root, "ghost.mp3")));

        int progressEvents = 0;
        using var cts = new CancellationTokenSource();
        _scanner.ScanProgressChanged += (_, _) =>
        {
            Interlocked.Increment(ref progressEvents);
            cts.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _scanner.ScanAsync(root, cts.Token));

        Assert.True(Volatile.Read(ref progressEvents) >= 1, "cancellation landed before the walk started");
        var all = await _dbContext.GetAllTracksAsync();
        Assert.Contains(all, t => t.Id == "tr_midghost"); // survived: no reconciliation ran
    }
}
