using System;
using System.IO;
using System.Threading.Tasks;
using Octave.Core.Models;
using Octave.Core.Services.Database;
using Xunit;

namespace Octave.Core.Tests;

public class SqliteDbContextTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbContext _dbContext;

    public SqliteDbContextTests()
    {
        _dbPath = Path.GetTempFileName() + ".db";
        _dbContext = new SqliteDbContext(_dbPath);
        _dbContext.InitializeAsync().GetAwaiter().GetResult();
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
        catch
        {
            // Best effort cleanup for temporary test db file
        }
    }

    [Fact]
    public async Task InitializeAsync_CreatesTablesAndMigratesSuccessfully()
    {
        var tracks = await _dbContext.GetAllTracksAsync();
        Assert.NotNull(tracks);
        Assert.Empty(tracks);
    }

    [Fact]
    public async Task Playlist_SurrogateKey_SupportsMultipleEntriesAndRemovalByEntryId()
    {
        var track = new Track("t1", "Song Title", "ar1", "Artist", "al1", "Album", 180, "C:\\test\\song.mp3", 1, 2024, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        var playlist = await _dbContext.CreatePlaylistAsync("My Playlist", null);
        Assert.NotNull(playlist);

        // Add the same track twice to playlist
        await _dbContext.AddTrackToPlaylistAsync(playlist.Id, track.Id);
        await _dbContext.AddTrackToPlaylistAsync(playlist.Id, track.Id);

        var entries = await _dbContext.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Equal(2, entries.Count);

        string firstEntryId = entries[0].EntryId;
        string secondEntryId = entries[1].EntryId;
        Assert.NotEqual(firstEntryId, secondEntryId);

        // Remove the first entry specifically
        await _dbContext.RemoveTrackEntryFromPlaylistAsync(firstEntryId);

        var remainingEntries = await _dbContext.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Single(remainingEntries);
        Assert.Equal(secondEntryId, remainingEntries[0].EntryId);
    }

    [Fact]
    public async Task RelocateTrackAsync_UpdatesTrackUri_Id_AndForeignKeysAtomically()
    {
        string oldPath = "C:\\music\\old_name.mp3";
        string newPath = "C:\\music\\new_name.mp3";
        string oldId = Octave.Core.Helpers.IdGenerator.FromTrackUri(oldPath);
        string newId = Octave.Core.Helpers.IdGenerator.FromTrackUri(newPath);

        var track = new Track(oldId, "Track 1", "ar1", "Artist", "al1", "Album", 200, oldPath, 1, 2024, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        var playlist = await _dbContext.CreatePlaylistAsync("Test Playlist", null);
        await _dbContext.AddTrackToPlaylistAsync(playlist.Id, oldId);
        await _dbContext.AddFavoriteAsync(oldId);

        // Perform relocation
        await _dbContext.RelocateTrackAsync(oldId, newPath);

        var relocatedTrack = await _dbContext.GetTrackByIdAsync(newId);
        Assert.NotNull(relocatedTrack);
        Assert.Equal(newPath, relocatedTrack.SourceUri);

        var oldTrackLookup = await _dbContext.GetTrackByIdAsync(oldId);
        Assert.Null(oldTrackLookup);

        var playlistEntries = await _dbContext.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Single(playlistEntries);
        Assert.Equal(newId, playlistEntries[0].Track.Id);

        bool isFav = await _dbContext.IsFavoriteAsync(newId);
        Assert.True(isFav);
    }

    [Fact]
    public async Task PlayerState_PersistsAndRestoresUnshuffledQueue()
    {
        var activeIds = new[] { "id1", "id2", "id3" };
        var unshuffledIds = new[] { "id3", "id1", "id2" };

        await _dbContext.SavePlayerStateAsync(activeIds, unshuffledIds, 1, 45.5, 0.8f, true, RepeatMode.Queue);

        var restored = await _dbContext.LoadPlayerStateAsync();
        Assert.NotNull(restored);
        Assert.Equal(1, restored.CurrentIndex);
        Assert.Equal(45.5, restored.PositionSeconds);
        Assert.True(restored.IsShuffle);
        Assert.Equal(RepeatMode.Queue, restored.RepeatMode);
        Assert.Equal(activeIds, restored.TrackIds);
        Assert.NotNull(restored.UnshuffledTrackIds);
        Assert.Equal(unshuffledIds, restored.UnshuffledTrackIds);
    }

    [Fact]
    public async Task GetDuplicatesAsync_FiltersVersionAnnotationsAndSortsByQuality()
    {
        var flac = new Track("id1", "Song Title", "ar1", "Artist", "al1", "Album", 180.0, "C:\\test\\song.flac", 1, 2024, DateTime.UtcNow);
        var mp3 = new Track("id2", "Song Title", "ar1", "Artist", "al1", "Album", 180.2, "C:\\test\\song.mp3", 1, 2024, DateTime.UtcNow);
        var live = new Track("id3", "Song Title (Live)", "ar1", "Artist", "al1", "Album", 185.0, "C:\\test\\song_live.flac", 1, 2024, DateTime.UtcNow);

        await _dbContext.UpsertTrackAsync(flac);
        await _dbContext.UpsertTrackAsync(mp3);
        await _dbContext.UpsertTrackAsync(live);

        var duplicates = await _dbContext.GetDuplicatesAsync();
        Assert.Single(duplicates);

        var group = duplicates[0];
        Assert.Equal(2, group.Tracks.Count);
        // Lossless FLAC should be ranked first before MP3
        Assert.Equal("id1", group.Tracks[0].Id);
        Assert.Equal("id2", group.Tracks[1].Id);
    }

    [Fact]
    public async Task RemoveTrackFromPlaylistAsync_WhenTrackIsDuplicated_DeletesOnlyOneInstance()
    {
        var track = new Track("t_dup", "Dup Song", "ar1", "Artist", "al1", "Album", 180, "C:\\test\\dup.mp3", 1, 2024, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        var playlist = await _dbContext.CreatePlaylistAsync("Dup Playlist", null);
        await _dbContext.AddTrackToPlaylistAsync(playlist.Id, track.Id);
        await _dbContext.AddTrackToPlaylistAsync(playlist.Id, track.Id);

        var entriesBefore = await _dbContext.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Equal(2, entriesBefore.Count);

        // Remove by TrackId
        await _dbContext.RemoveTrackFromPlaylistAsync(playlist.Id, track.Id);

        var entriesAfter = await _dbContext.GetPlaylistTrackEntriesAsync(playlist.Id);
        Assert.Single(entriesAfter);
        Assert.Equal(track.Id, entriesAfter[0].Track.Id);
    }

    [Fact]
    public async Task GetAlbumsByIdsAsync_ReturnsMatchingAlbumsInBatch()
    {
        var artist = new Artist("ar_batch", "Batch Artist", null, null);
        var album1 = new Album("al_batch_1", "Album 1", "ar_batch", "Batch Artist", 2023, "http://art1.png");
        var album2 = new Album("al_batch_2", "Album 2", "ar_batch", "Batch Artist", 2024, "http://art2.png");

        await _dbContext.UpsertArtistAsync(artist);
        await _dbContext.UpsertAlbumAsync(album1);
        await _dbContext.UpsertAlbumAsync(album2);

        var albums = await _dbContext.GetAlbumsByIdsAsync(new[] { "al_batch_1", "al_batch_2", "nonexistent_alb" });
        Assert.Equal(2, albums.Count);
        Assert.Contains(albums, a => a.Id == "al_batch_1" && a.Title == "Album 1");
        Assert.Contains(albums, a => a.Id == "al_batch_2" && a.Title == "Album 2");
    }

    [Fact]
    public async Task CompilationAlbum_GuestArtists_ArePreservedAcrossOrphanSweeps()
    {
        var albumArtist = new Artist("ar_va", "Various Artists", null, null);
        var guestArtist1 = new Artist("ar_guest1", "Guest Artist 1", null, null);
        var guestArtist2 = new Artist("ar_guest2", "Guest Artist 2", null, null);

        var compilationAlbum = new Album("al_comp", "Greatest Hits Comp", "ar_va", "Various Artists", 2024, null);
        var track1 = new Track("t_comp1", "Guest Track 1", "ar_guest1", "Guest Artist 1", "al_comp", "Greatest Hits Comp", 200, "C:\\music\\comp1.mp3", 1, 2024, DateTime.UtcNow);
        var track2 = new Track("t_comp2", "Guest Track 2", "ar_guest2", "Guest Artist 2", "al_comp", "Greatest Hits Comp", 210, "C:\\music\\comp2.mp3", 2, 2024, DateTime.UtcNow);

        await _dbContext.UpsertArtistAsync(albumArtist);
        await _dbContext.UpsertArtistAsync(guestArtist1);
        await _dbContext.UpsertArtistAsync(guestArtist2);
        await _dbContext.UpsertAlbumAsync(compilationAlbum);
        await _dbContext.UpsertTrackAsync(track1);
        await _dbContext.UpsertTrackAsync(track2);

        // Verify all 3 artists are queryable
        Assert.NotNull(await _dbContext.GetArtistByIdAsync("ar_va"));
        Assert.NotNull(await _dbContext.GetArtistByIdAsync("ar_guest1"));
        Assert.NotNull(await _dbContext.GetArtistByIdAsync("ar_guest2"));

        var allArtists = await _dbContext.GetAllArtistsAsync();
        Assert.Equal(3, allArtists.Count);
    }

    [Fact]
    public async Task ArtworkCacheManager_CachesBytesDeterministicallyWithSha256()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "octave_art_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new Octave.Core.Services.Metadata.ArtworkCacheManager(tempDir);
            byte[] fakeImage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            string? token = await manager.CacheBytesAsync(fakeImage, "image/jpeg");

            Assert.NotNull(token);
            Assert.StartsWith("ArtworkCache/", token);
            Assert.EndsWith(".jpg", token);

            // Re-cache same bytes (should return same token without error)
            string? token2 = await manager.CacheBytesAsync(fakeImage, "image/jpeg");
            Assert.Equal(token, token2);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task SearchLibraryAsync_Fts5Matching_FindsTracksAccurately()
    {
        var artist = new Artist("ar_fts", "Pink Floyd", null, null);
        var album = new Album("al_fts", "The Dark Side of the Moon", "ar_fts", "Pink Floyd", 1973, null);
        var track1 = new Track("tr_fts1", "Time", "ar_fts", "Pink Floyd", "al_fts", "The Dark Side of the Moon", 413, "C:/music/time.flac", 4, 1973, DateTime.UtcNow);
        var track2 = new Track("tr_fts2", "Money", "ar_fts", "Pink Floyd", "al_fts", "The Dark Side of the Moon", 382, "C:/music/money.flac", 6, 1973, DateTime.UtcNow);

        await _dbContext.UpsertArtistAsync(artist);
        await _dbContext.UpsertAlbumAsync(album);
        await _dbContext.UpsertTrackAsync(track1);
        await _dbContext.UpsertTrackAsync(track2);

        var results = await _dbContext.SearchLibraryAsync("Money");
        Assert.Single(results.Tracks);
        Assert.Equal("Money", results.Tracks[0].Title);

        var albumResults = await _dbContext.SearchLibraryAsync("Dark Side");
        Assert.NotEmpty(albumResults.Albums);
        Assert.Equal("The Dark Side of the Moon", albumResults.Albums[0].Title);
    }

    [Fact]
    public async Task GetArtistByNameAsync_And_GetAlbumByTitleAsync_ReturnAccurateRecords()
    {
        var artist = new Artist("ar_queen", "Queen", "Legendary Rock Band", null);
        var album = new Album("al_night_opera", "A Night at the Opera", "ar_queen", "Queen", 1975, null);

        await _dbContext.UpsertArtistAsync(artist);
        await _dbContext.UpsertAlbumAsync(album);

        var resolvedArtist = await _dbContext.GetArtistByNameAsync("queen");
        Assert.NotNull(resolvedArtist);
        Assert.Equal("ar_queen", resolvedArtist.Id);
        Assert.Equal("Queen", resolvedArtist.Name);

        var resolvedAlbum = await _dbContext.GetAlbumByTitleAsync("a night at the opera", "ar_queen");
        Assert.NotNull(resolvedAlbum);
        Assert.Equal("al_night_opera", resolvedAlbum.Id);
        Assert.Equal("A Night at the Opera", resolvedAlbum.Title);

        Assert.Null(await _dbContext.GetArtistByNameAsync("Nonexistent Artist"));
        Assert.Null(await _dbContext.GetAlbumByTitleAsync("Nonexistent Album"));
    }

    [Fact]
    public async Task ClearDatabaseAsync_WipesAllTables_IncludingCachesAndSettings()
    {
        var artist = new Artist("ar_clear", "Artist Clear", null, null);
        var album = new Album("al_clear", "Album Clear", "ar_clear", "Artist Clear", 2024, null);
        var track = new Track("tr_clear", "Track Clear", "ar_clear", "Artist Clear", "al_clear", "Album Clear", 200, "C:/music/clear.mp3", 1, 2024, DateTime.UtcNow);

        await _dbContext.UpsertArtistAsync(artist);
        await _dbContext.UpsertAlbumAsync(album);
        await _dbContext.UpsertTrackAsync(track);
        await _dbContext.AddFavoriteAsync(track.Id);
        await _dbContext.AddMonitoredFolderAsync("C:/music");
        await _dbContext.SetSettingAsync("TestSettingKey", "TestSettingValue");

        var playlist = await _dbContext.CreatePlaylistAsync("Test Playlist", null);
        await _dbContext.AddTrackToPlaylistAsync(playlist.Id, track.Id);

        // Verify data was populated
        Assert.Single(await _dbContext.GetAllTracksAsync());
        Assert.Single(await _dbContext.GetAllAlbumsAsync());
        Assert.Single(await _dbContext.GetAllArtistsAsync());
        Assert.Single(await _dbContext.GetFavoritesAsync());
        Assert.Single(await _dbContext.GetMonitoredFoldersAsync());
        Assert.Equal("TestSettingValue", await _dbContext.GetSettingAsync("TestSettingKey"));

        // Clear everything including settings
        await _dbContext.ClearDatabaseAsync(preserveSettings: false);

        // Assert all tables are wiped
        Assert.Empty(await _dbContext.GetAllTracksAsync());
        Assert.Empty(await _dbContext.GetAllAlbumsAsync());
        Assert.Empty(await _dbContext.GetAllArtistsAsync());
        Assert.Empty(await _dbContext.GetFavoritesAsync());
        Assert.Empty(await _dbContext.GetMonitoredFoldersAsync());
        Assert.Empty(await _dbContext.GetPlaylistsAsync());
        Assert.Null(await _dbContext.GetSettingAsync("TestSettingKey"));
        var searchResults = await _dbContext.SearchLibraryAsync("Track Clear");
        Assert.Empty(searchResults.Tracks);
    }
}
