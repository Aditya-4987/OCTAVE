using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Octave.Core.Models;
using Octave.Core.Services.Cache;
using Octave.Core.Services.Database;
using Xunit;

namespace Octave.Core.Tests;

/// <summary>
/// Acceptance tests for the SQLite concurrency root batch (CODEBASE_AUDIT §15 Batch 1):
/// busy_timeout under concurrent writers (DB-01), atomic upserts (DB-04), transactional
/// PlaylistTracks migration (DB-05), FTS/LIKE fallback semantics (DB-03), empty-query
/// guard + default limit (DB-07), duplicate-safe playlist reorder (DB-08), and graceful
/// L2 cache degradation on DB failure (CACHE-01).
/// </summary>
public class SqliteConcurrencyAndMigrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbContext _dbContext;

    public SqliteConcurrencyAndMigrationTests()
    {
        // Guid-named path (not Path.GetTempFileName) — GetTempFileName creates a 0-byte
        // file we would never delete, leaking one per test instance.
        _dbPath = Path.Combine(Path.GetTempPath(), $"octave_b1_{Guid.NewGuid():N}.db");
        _dbContext = new SqliteDbContext(_dbPath);
        _dbContext.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        // WAL mode leaves -wal/-shm sidecars next to the db; sweep all three.
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best effort cleanup for temporary test db file
            }
        }
    }

    private static Track MakeTrack(string id, string title) =>
        new(id, title, "ar_" + id, "Artist " + id, "al_" + id, "Album", 180.0,
            "C:\\music\\" + id + ".mp3", "local", 1, 2024, DateTime.UtcNow);

    [Fact]
    public async Task ConcurrentWriters_DoNotThrowSqliteBusy_AndPreserveAllRows()
    {
        // Seed a few tracks so readers/writers collide over non-empty tables.
        for (int i = 0; i < 5; i++)
        {
            await _dbContext.UpsertTrackAsync(MakeTrack($"seed{i}", $"Seed Song {i}"));
        }

        const int writerCount = 8;
        const int writesPerWriter = 10;

        var tasks = Enumerable.Range(0, writerCount).Select(async w =>
        {
            for (int i = 0; i < writesPerWriter; i++)
            {
                await _dbContext.UpsertTrackAsync(MakeTrack($"w{w}_t{i}", $"Writer {w} Song {i}"));
                // Interleave reads to exercise the reader/writer mix WAL is meant to allow.
                if (i % 3 == 0)
                {
                    await _dbContext.GetAllTracksAsync();
                }
            }
        });

        // Without PRAGMA busy_timeout any overlapping writer pair fails fast with
        // SQLITE_BUSY ("database is locked") — this aggregation surfaces it.
        await Task.WhenAll(tasks);

        var all = await _dbContext.GetAllTracksAsync();
        Assert.Equal(writerCount * writesPerWriter + 5, all.Count);
    }

    [Fact]
    public async Task InitializeAsync_LegacyPlaylistTracksSchema_PreservesAllRowsWithSurrogateIds()
    {
        string legacyDbPath = Path.Combine(Path.GetTempPath(), $"octave_b1_legacy_{Guid.NewGuid():N}.db");
        try
        {
            // Build a pre-migration database whose PlaylistTracks has no surrogate Id column.
            using (var raw = new SqliteConnection($"Data Source={legacyDbPath}"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE Playlists (
                        Id TEXT PRIMARY KEY,
                        Title TEXT NOT NULL COLLATE NOCASE,
                        Description TEXT,
                        CreatedAt INTEGER NOT NULL,
                        IsLocalOnly INTEGER NOT NULL CHECK(IsLocalOnly IN (0, 1))
                    ) STRICT;
                    CREATE TABLE Tracks (
                        Id TEXT PRIMARY KEY,
                        Title TEXT NOT NULL COLLATE NOCASE,
                        ArtistId TEXT NOT NULL,
                        ArtistName TEXT NOT NULL,
                        AlbumId TEXT NOT NULL,
                        AlbumTitle TEXT NOT NULL,
                        DurationSeconds REAL NOT NULL,
                        SourceUri TEXT NOT NULL,
                        Provider TEXT NOT NULL,
                        TrackNumber INTEGER NOT NULL DEFAULT 1,
                        Year INTEGER NOT NULL,
                        DateAdded INTEGER NOT NULL,
                        Genre TEXT NOT NULL DEFAULT '',
                        ReplayGain REAL NOT NULL DEFAULT 0.0
                    ) STRICT;
                    CREATE TABLE PlaylistTracks (
                        PlaylistId TEXT NOT NULL,
                        TrackId TEXT NOT NULL,
                        SortOrder INTEGER NOT NULL
                    ) STRICT;
                    INSERT INTO Playlists VALUES ('pl1', 'Legacy Playlist', NULL, 1700000000, 1);
                    INSERT INTO Tracks VALUES ('t1', 'Song', 'ar1', 'Artist', 'al1', 'Album', 180.0, 'C:\\x.mp3', 'local', 1, 2024, 1700000000, '', 0.0);
                    INSERT INTO PlaylistTracks VALUES ('pl1', 't1', 0);
                    INSERT INTO PlaylistTracks VALUES ('pl1', 't1', 1);
                    INSERT INTO PlaylistTracks VALUES ('pl1', 't1', 2);";
                cmd.ExecuteNonQuery();
            }

            var migratedContext = new SqliteDbContext(legacyDbPath);
            await migratedContext.InitializeAsync();

            // The destructive rebuild (DROP + RENAME inside InitializeAsync) must not lose rows.
            var entries = await migratedContext.GetPlaylistTrackEntriesAsync("pl1");
            Assert.Equal(3, entries.Count);
            Assert.All(entries, e => Assert.False(string.IsNullOrEmpty(e.EntryId)));
            Assert.Equal(3, entries.Select(e => e.EntryId).Distinct().Count());
            Assert.Equal(new[] { 0, 1, 2 }, entries.Select(e => e.SortOrder));
        }
        finally
        {
            foreach (var file in new[] { legacyDbPath, legacyDbPath + "-wal", legacyDbPath + "-shm" })
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
        }
    }

    [Fact]
    public async Task SetPlaylistOrderAsync_ByEntryId_KeepsDuplicateTrackCopiesDistinct()
    {
        var track = MakeTrack("dup1", "Duplicate Song");
        await _dbContext.UpsertTrackAsync(track);

        var playlist = await _dbContext.CreatePlaylistAsync("Reorder Playlist", null);
        await _dbContext.AddTrackToPlaylistAsync(playlist.Id, track.Id);
        await _dbContext.AddTrackToPlaylistAsync(playlist.Id, track.Id);

        var entries = await _dbContext.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Equal(2, entries.Count);
        string firstEntryId = entries[0].EntryId;
        string secondEntryId = entries[1].EntryId;

        // Reorder by surrogate entry ids: second copy moves to front.
        await _dbContext.SetPlaylistOrderAsync(playlist.Id, new[] { secondEntryId, firstEntryId });

        var reordered = await _dbContext.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Equal(2, reordered.Count);
        Assert.Equal(secondEntryId, reordered[0].EntryId);
        Assert.Equal(firstEntryId, reordered[1].EntryId);
        Assert.NotEqual(reordered[0].SortOrder, reordered[1].SortOrder);
    }

    [Fact]
    public async Task SetPlaylistOrderAsync_ByTrackId_DoesNotCollapseRepeatedTrackToOneSlot()
    {
        var track = MakeTrack("dup2", "Repeated Song");
        await _dbContext.UpsertTrackAsync(track);

        var playlist = await _dbContext.CreatePlaylistAsync("Dup Reorder Playlist", null);
        await _dbContext.AddTrackToPlaylistAsync(playlist.Id, track.Id);
        await _dbContext.AddTrackToPlaylistAsync(playlist.Id, track.Id);

        // The old bulk UPDATE matched `Id = @key OR TrackId = @key`, stamping BOTH copies
        // with the same SortOrder and making their relative order undefined.
        await _dbContext.SetPlaylistOrderAsync(playlist.Id, new[] { track.Id });

        var entries = await _dbContext.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Equal(2, entries.Count);
        Assert.NotEqual(entries[0].SortOrder, entries[1].SortOrder);
        // The first copy in current order receives position 0.
        Assert.Equal(0, entries[0].SortOrder);
    }

    [Fact]
    public async Task SearchLibraryAsync_EmptyOrWhitespaceQuery_ReturnsEmptyResultsNotWholeLibrary()
    {
        await _dbContext.UpsertTrackAsync(MakeTrack("s1", "Alpha Song"));
        await _dbContext.UpsertTrackAsync(MakeTrack("s2", "Beta Song"));

        var empty = await _dbContext.SearchLibraryAsync("");
        Assert.NotNull(empty.Tracks);
        Assert.NotNull(empty.Albums);
        Assert.NotNull(empty.Artists);
        Assert.NotNull(empty.Playlists);
        Assert.Empty(empty.Tracks);
        Assert.Empty(empty.Albums);
        Assert.Empty(empty.Artists);
        Assert.Empty(empty.Playlists);

        var whitespace = await _dbContext.SearchLibraryAsync("   ");
        Assert.NotNull(whitespace.Tracks);
        Assert.NotNull(whitespace.Albums);
        Assert.NotNull(whitespace.Artists);
        Assert.NotNull(whitespace.Playlists);
        Assert.Empty(whitespace.Tracks);
        Assert.Empty(whitespace.Albums);
        Assert.Empty(whitespace.Artists);
        Assert.Empty(whitespace.Playlists);
    }

    [Fact]
    public async Task SearchLibraryAsync_WithoutExplicitLimit_IsBounded()
    {
        // Seed more tracks than the default cap; every title shares a searchable token.
        for (int i = 0; i < 220; i++)
        {
            await _dbContext.UpsertTrackAsync(MakeTrack($"bulk{i}", $"Commonword Song {i}"));
        }

        var results = await _dbContext.SearchLibraryAsync("Commonword");
        Assert.True(results.Tracks.Count <= 200, $"expected bounded results, got {results.Tracks.Count}");
    }

    [Fact]
    public async Task SearchLibraryAsync_FtsRanButFoundNothing_SkipsLikeFallback()
    {
        await _dbContext.UpsertTrackAsync(MakeTrack("m1", "Money"));

        // "oney" cannot match FTS prefix semantics (no token starts with "oney"),
        // but LIKE '%oney%' would match "Money". Once FTS executes successfully the
        // leading-wildcard LIKE scan must not also run (DB-03).
        var results = await _dbContext.SearchLibraryAsync("oney");
        Assert.Empty(results.Tracks);

        // Prefix queries still work through FTS.
        var prefix = await _dbContext.SearchLibraryAsync("Mone");
        Assert.Single(prefix.Tracks);
        Assert.Equal("Money", prefix.Tracks[0].Title);
    }

    [Fact]
    public async Task TwoTierExternalDataCache_OnL2DatabaseFailure_ReturnsMissInsteadOfThrowing()
    {
        var cache = new TwoTierExternalDataCache(_dbContext);

        var payload = new[] { "a", "b" };
        await cache.SetAsync("key1", payload);
        Assert.NotNull(await cache.GetAsync<string[]>("key1"));

        // Simulate severe DB damage: the cache table itself disappears.
        using (var raw = new SqliteConnection($"Data Source={_dbPath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "DROP TABLE ExternalDataCache;";
            cmd.ExecuteNonQuery();
        }

        // A fresh instance has a cold L1, forcing the L2 read path: reads must degrade
        // to a cache miss (CACHE-01), not throw SqliteException ("no such table").
        var coldCache = new TwoTierExternalDataCache(_dbContext);
        Assert.Null(await coldCache.GetAsync<string[]>("key1"));
        Assert.Null(await coldCache.GetAsync<string[]>("never_cached"));

        // Writes must stay non-fatal too.
        await coldCache.SetAsync("key2", new[] { "c" });
    }
}
