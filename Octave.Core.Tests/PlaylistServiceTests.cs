using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Octave.Core.Models;
using Octave.Core.Services.Database;
using Octave.Core.Services.Playback;
using Xunit;

namespace Octave.Core.Tests;

/// <summary>
/// TEST-16: the playlist pipeline (create → add → reorder → remove entry →
/// delete cascade) previously had no direct coverage at the service layer. These
/// tests drive the REAL SqliteDbContext through PlaylistService, pinning the
/// facade wiring AND the ordering contract (DB-08: one position per entry, so a
/// duplicated track keeps distinct sort orders).
/// </summary>
public class PlaylistServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDbContext _dbContext;
    private readonly PlaylistService _service;

    public PlaylistServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_PlistSvcTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbContext = new SqliteDbContext(Path.Combine(_tempDir, "plist.db"));
        _dbContext.InitializeAsync().GetAwaiter().GetResult();
        _service = new PlaylistService(_dbContext);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static Track MakeTrack(string id, int n) =>
        new(id, $"Song {id}", "ar" + id, $"Artist {id}", "al", "Shared Album", 180.0,
            $"http://test/{id}.mp3", n, 2024, DateTime.UtcNow);

    private async Task<Playlist> CreateWithTracksAsync(params string[] trackIds)
    {
        for (int i = 0; i < trackIds.Length; i++)
        {
            await _dbContext.UpsertTrackAsync(MakeTrack(trackIds[i], i + 1));
        }
        return await _service.CreatePlaylistAsync("Test List");
    }

    [Fact]
    public async Task CreateRenameDelete_RoundTrip_RaisesPlaylistsChangedEachStep()
    {
        int changed = 0;
        _service.PlaylistsChanged += (_, _) => changed++;

        var playlist = await _service.CreatePlaylistAsync("Road Trip", "desc");
        Assert.NotNull(playlist);
        Assert.Equal("Road Trip", playlist.Title);
        Assert.Equal(1, changed);

        await _service.RenamePlaylistAsync(playlist.Id, "Highway Tunes");
        var renamed = await _service.GetPlaylistByIdAsync(playlist.Id);
        Assert.Equal("Highway Tunes", renamed!.Title);
        Assert.Equal(2, changed);

        await _service.DeletePlaylistAsync(playlist.Id);
        Assert.Null(await _service.GetPlaylistByIdAsync(playlist.Id));
        Assert.Equal(3, changed);
    }

    [Fact]
    public async Task AddTracks_EntriesAppearInInsertionOrder()
    {
        var playlist = await CreateWithTracksAsync("t1", "t2", "t3");

        foreach (var id in new[] { "t1", "t2", "t3" })
        {
            await _service.AddTrackAsync(playlist.Id, id);
        }

        var entries = await _service.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Equal(new[] { "t1", "t2", "t3" }, entries.Select(e => e.Track.Id).ToArray());
        Assert.True(entries.Select(e => e.SortOrder).SequenceEqual(entries.Select(e => e.SortOrder).OrderBy(x => x)));
        Assert.All(entries, e => Assert.Equal(playlist.Id, e.PlaylistId));

        var hydrated = await _service.GetPlaylistTracksAsync(playlist.Id);
        Assert.Equal(new[] { "t1", "t2", "t3" }, hydrated.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task SetOrderByTrackIds_ReordersAndSurvivesDuplicateEntries()
    {
        // DB-08 regression guard: "dup" appears TWICE in the playlist. The old bulk
        // update stamped every copy with the same SortOrder; the current contract is
        // one position per entry.
        var playlist = await CreateWithTracksAsync("t1", "t2", "dup");
        await _service.AddTrackAsync(playlist.Id, "t1");
        await _service.AddTrackAsync(playlist.Id, "dup");
        await _service.AddTrackAsync(playlist.Id, "t2");
        await _service.AddTrackAsync(playlist.Id, "dup");   // duplicate copy

        var before = await _service.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Equal(new[] { "t1", "dup", "t2", "dup" }, before.Select(e => e.Track.Id).ToArray());

        // Reorder using TRACK ids (the API accepts either form). Each key positions
        // its FIRST not-yet-positioned copy; extra copies keep their existing
        // SortOrder — so t2 lands first and the untouched dup trails at the end.
        await _service.SetOrderAsync(playlist.Id, new[] { "t2", "dup", "t1" });

        var after = await _service.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Equal(new[] { "t2", "dup", "t1", "dup" }, after.Select(e => e.Track.Id).ToArray());

        // Every entry — including both dup copies — still owns a DISTINCT position.
        Assert.Equal(after.Count, after.Select(e => e.SortOrder).Distinct().Count());
        int[] dupPositions = after.Where(e => e.Track.Id == "dup").Select(e => e.SortOrder).OrderBy(x => x).ToArray();
        Assert.True(dupPositions[0] < dupPositions[1]);     // copies kept their relative order
    }

    [Fact]
    public async Task RemoveTrackEntry_RemovesSingleCopy_OtherCopiesStay()
    {
        var playlist = await CreateWithTracksAsync("solo", "twice");
        await _service.AddTrackAsync(playlist.Id, "solo");
        await _service.AddTrackAsync(playlist.Id, "twice");
        await _service.AddTrackAsync(playlist.Id, "twice"); // second copy

        var entries = await _service.GetPlaylistTrackEntriesAsync(playlist.Id);
        var secondCopy = entries.Last(e => e.Track.Id == "twice");

        await _service.RemoveTrackEntryAsync(secondCopy.EntryId);

        var after = await _service.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Single(after, e => e.Track.Id == "twice");   // first copy survives
        Assert.Contains(after, e => e.Track.Id == "solo");
        Assert.DoesNotContain(after, e => e.EntryId == secondCopy.EntryId);

        // Removing by TRACK id clears exactly ONE occurrence (DB contract: the
        // surrogate-Id match wins, else LIMIT 1 by rowid).
        await _service.RemoveTrackAsync(playlist.Id, "twice");
        var final = await _service.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.DoesNotContain(final, e => e.Track.Id == "twice");
        Assert.Contains(final, e => e.Track.Id == "solo");
    }

    [Fact]
    public async Task DeletePlaylist_CascadesTrackEntries()
    {
        var playlist = await CreateWithTracksAsync("t1", "t2");
        await _service.AddTrackAsync(playlist.Id, "t1");
        await _service.AddTrackAsync(playlist.Id, "t2");

        await _service.DeletePlaylistAsync(playlist.Id);

        // Entries die with their playlist; the underlying TRACKS remain in the library.
        Assert.Empty(await _service.GetPlaylistTrackEntriesAsync(playlist.Id));
        Assert.NotNull(await _dbContext.GetTrackByIdAsync("t1"));
        Assert.NotNull(await _dbContext.GetTrackByIdAsync("t2"));
    }

    [Fact]
    public async Task AddTracksAsync_BulkInsertsInOrder_AndMaintainsTrackCount()
    {
        var playlist = await CreateWithTracksAsync("b1", "b2", "b3");
        await _service.AddTracksAsync(playlist.Id, new[] { "b1", "b2", "b3" });

        var entries = await _service.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Equal(3, entries.Count);
        Assert.Equal("b1", entries[0].Track.Id);
        Assert.Equal("b2", entries[1].Track.Id);
        Assert.Equal("b3", entries[2].Track.Id);

        // Appending more tracks continues sort order without conflict
        await _service.AddTracksAsync(playlist.Id, new[] { "b1" });
        var updated = await _service.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Equal(4, updated.Count);
        Assert.Equal("b1", updated[3].Track.Id);
    }

    [Fact]
    public async Task EmptyOrWhitespaceIds_NoOpSafely()
    {
        int events = 0;
        _service.PlaylistsChanged += (_, _) => events++;

        await _service.DeletePlaylistAsync("");
        await _service.RenamePlaylistAsync("", "New Name");
        await _service.AddTrackAsync("", "t1");
        await _service.AddTracksAsync("", new[] { "t1" });
        await _service.RemoveTrackAsync("", "t1");
        await _service.RemoveTrackEntryAsync("");
        await _service.SetOrderAsync("", new[] { "t1" });

        Assert.Equal(0, events);
    }
}
