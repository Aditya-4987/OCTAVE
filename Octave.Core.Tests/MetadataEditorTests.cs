using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Database;
using Octave.Core.Services.Library;
using Octave.Core.Services.Metadata;
using Xunit;

namespace Octave.Core.Tests;

public class MetadataEditorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDbContext _dbContext;

    private static readonly byte[] ValidMp3Bytes = new byte[] {
        // ID3v2.3 header (10 bytes: 'ID3', ver 3.0, flags 0, syncsafe size 10)
        0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A,
        // ID3 padding (10 bytes)
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        // MPEG 1 Layer 3 Sync word and header (128kbps 44.1kHz)
        0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    private static readonly byte[] SampleJpegBytes = new byte[] {
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x01, 0x00, 0x60, 0x00, 0x60, 0x00, 0x00, 0xFF, 0xD9
    };

    public MetadataEditorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_TagEditTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        string dbPath = Path.Combine(_tempDir, "test.db");
        _dbContext = new SqliteDbContext($"Data Source={dbPath}");
        _dbContext.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private async Task<(string FilePath, Track Track)> CreateTestTrackAsync(string fileName = "test.mp3")
    {
        string filePath = Path.Combine(_tempDir, fileName);
        await File.WriteAllBytesAsync(filePath, ValidMp3Bytes);

        // Populate initial tags
        using (var tagFile = TagLib.File.Create(filePath))
        {
            tagFile.Tag.Title = "Initial Title";
            tagFile.Tag.Performers = new[] { "Initial Artist" };
            tagFile.Tag.Album = "Initial Album";
            tagFile.Tag.Year = 2000;
            tagFile.Tag.Track = 1;
            tagFile.Save();
        }

        var track = new Track(
            "tr_test_1",
            "Initial Title",
            "ar_init",
            "Initial Artist",
            "al_init",
            "Initial Album",
            120.0,
            filePath,
            "Local",
            1,
            2000,
            DateTime.UtcNow,
            "Rock",
            0.0f);

        await _dbContext.UpsertTrackAsync(track);
        return (filePath, track);
    }

    // =================================================================
    // 1. METADATA ROUND-TRIPPING (All Tags & MusicBrainz IDs)
    // =================================================================

    [Fact]
    public async Task UpdateTrackMetadataAsync_RoundTripsAllMetadataFieldsSuccessfully()
    {
        var (filePath, track) = await CreateTestTrackAsync("roundtrip.mp3");
        var mockLibraryService = new Mock<ILibraryService>();
        var artworkCache = new ArtworkCacheManager(_tempDir);
        var editor = new TrackMetadataEditor(_dbContext, mockLibraryService.Object, artworkCache);

        var extIds = new ExternalIds(
            MusicBrainzId: "mb-rec-999",
            Isrc: "USRC17607839",
            AdditionalIds: new Dictionary<string, string>
            {
                ["MusicBrainzReleaseId"] = "mb-rel-888",
                ["MusicBrainzArtistId"] = "mb-art-777",
                ["MusicBrainzReleaseGroupId"] = "mb-rg-666"
            });

        var update = new TrackMetadataUpdate(
            Title: "Updated Song Title",
            ArtistName: "Updated Artist Name",
            AlbumTitle: "Updated Album Name",
            AlbumArtist: "Updated Album Artist",
            Composer: "Updated Composer",
            Genre: "Progressive Rock",
            Year: 2024,
            TrackNumber: 5,
            TrackCount: 12,
            DiscNumber: 1,
            DiscCount: 2,
            Comment: "Remastered in 2024",
            Lyrics: "[00:01.00] New lyric line",
            ExternalIds: extIds,
            ReplayGain: -2.5f);

        var result = await editor.UpdateTrackMetadataAsync(track.Id, update);

        Assert.True(result.Success);
        Assert.True(result.FileResult.Success);
        Assert.NotNull(result.DbResult);
        Assert.True(result.DbResult.Success);

        // 1. Verify file tags directly on disk via TagLib#
        using (var reread = TagLib.File.Create(filePath))
        {
            Assert.Equal("Updated Song Title", reread.Tag.Title);
            Assert.Equal("Updated Artist Name", reread.Tag.Performers[0]);
            Assert.Equal("Updated Album Name", reread.Tag.Album);
            Assert.Equal("Updated Album Artist", reread.Tag.AlbumArtists[0]);
            Assert.Equal("Updated Composer", reread.Tag.Composers[0]);
            Assert.Equal("Progressive Rock", reread.Tag.Genres[0]);
            Assert.Equal((uint)2024, reread.Tag.Year);
            Assert.Equal((uint)5, reread.Tag.Track);
            Assert.Equal((uint)12, reread.Tag.TrackCount);
            Assert.Equal((uint)1, reread.Tag.Disc);
            Assert.Equal((uint)2, reread.Tag.DiscCount);
            Assert.Equal("Remastered in 2024", reread.Tag.Comment);
            Assert.Equal("[00:01.00] New lyric line", reread.Tag.Lyrics);
            Assert.Equal("mb-rec-999", reread.Tag.MusicBrainzTrackId);
            Assert.Equal("mb-rel-888", reread.Tag.MusicBrainzReleaseId);
            Assert.Equal("mb-art-777", reread.Tag.MusicBrainzArtistId);
        }

        // 2. Verify SQLite DB state
        var dbTrack = await _dbContext.GetTrackByIdAsync(track.Id);
        Assert.NotNull(dbTrack);
        Assert.Equal("Updated Song Title", dbTrack.Title);
        Assert.Equal("Updated Artist Name", dbTrack.ArtistName);
        Assert.Equal("Updated Album Name", dbTrack.AlbumTitle);
        Assert.Equal(5, dbTrack.TrackNumber);
        Assert.Equal(2024, dbTrack.Year);
        Assert.Equal(-2.5f, dbTrack.ReplayGain);
    }

    // =================================================================
    // 2. EMBEDDED ARTWORK UPDATE & CACHING
    // =================================================================

    [Fact]
    public async Task UpdateTrackMetadataAsync_UpdatesArtwork_AndCachesToken()
    {
        var (filePath, track) = await CreateTestTrackAsync("artwork_test.mp3");
        var mockLibraryService = new Mock<ILibraryService>();
        var artworkCache = new ArtworkCacheManager(_tempDir);
        var editor = new TrackMetadataEditor(_dbContext, mockLibraryService.Object, artworkCache);

        var update = new TrackMetadataUpdate(
            Title: "Title With Art",
            ArtistName: "Artist With Art",
            AlbumTitle: "Album With Art",
            NewArtworkBytes: SampleJpegBytes,
            ArtworkMimeType: "image/jpeg");

        var result = await editor.UpdateTrackMetadataAsync(track.Id, update);

        Assert.True(result.Success);

        // Verify picture exists in file tags
        using (var reread = TagLib.File.Create(filePath))
        {
            Assert.NotEmpty(reread.Tag.Pictures);
            Assert.Equal(SampleJpegBytes.Length, reread.Tag.Pictures[0].Data.Data.Length);
        }

        // Verify album in DB has valid cached ArtworkUrl
        var dbAlbum = await _dbContext.GetAlbumByIdAsync(result.DbResult!.UpdatedTrack!.AlbumId);
        Assert.NotNull(dbAlbum);
        Assert.NotNull(dbAlbum.ArtworkUrl);
        Assert.StartsWith("ArtworkCache/", dbAlbum.ArtworkUrl);
    }

    // =================================================================
    // 3. PRESERVE EXISTING ARTWORK WHEN NOT REPLACED
    // =================================================================

    [Fact]
    public async Task UpdateTrackMetadataAsync_PreservesExistingArtwork_WhenNoNewArtworkGiven()
    {
        var (filePath, track) = await CreateTestTrackAsync("preserve_art.mp3");

        // Seed initial artwork
        using (var tagFile = TagLib.File.Create(filePath))
        {
            var pic = new TagLib.Picture(new TagLib.ByteVector(SampleJpegBytes))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = "image/jpeg"
            };
            tagFile.Tag.Pictures = new TagLib.IPicture[] { pic };
            tagFile.Save();
        }

        var mockLibraryService = new Mock<ILibraryService>();
        var artworkCache = new ArtworkCacheManager(_tempDir);
        var editor = new TrackMetadataEditor(_dbContext, mockLibraryService.Object, artworkCache);

        // Update ONLY title and artist, with NewArtworkBytes = null and ClearArtwork = false
        var update = new TrackMetadataUpdate(
            Title: "Renamed Title",
            ArtistName: "Renamed Artist",
            AlbumTitle: "Renamed Album");

        var result = await editor.UpdateTrackMetadataAsync(track.Id, update);

        Assert.True(result.Success);

        // Verify artwork is still present in file!
        using (var reread = TagLib.File.Create(filePath))
        {
            Assert.NotEmpty(reread.Tag.Pictures);
            Assert.Equal(SampleJpegBytes.Length, reread.Tag.Pictures[0].Data.Data.Length);
        }
    }

    // =================================================================
    // 4. ARTWORK REMOVAL (ClearArtwork = true)
    // =================================================================

    [Fact]
    public async Task UpdateTrackMetadataAsync_ClearArtwork_RemovesEmbeddedPictures()
    {
        var (filePath, track) = await CreateTestTrackAsync("clear_art.mp3");

        // Seed initial artwork
        using (var tagFile = TagLib.File.Create(filePath))
        {
            var pic = new TagLib.Picture(new TagLib.ByteVector(SampleJpegBytes))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = "image/jpeg"
            };
            tagFile.Tag.Pictures = new TagLib.IPicture[] { pic };
            tagFile.Save();
        }

        var mockLibraryService = new Mock<ILibraryService>();
        var artworkCache = new ArtworkCacheManager(_tempDir);
        var editor = new TrackMetadataEditor(_dbContext, mockLibraryService.Object, artworkCache);

        var update = new TrackMetadataUpdate(
            Title: "No Art Song",
            ArtistName: "Artist",
            AlbumTitle: "Album",
            ClearArtwork: true);

        var result = await editor.UpdateTrackMetadataAsync(track.Id, update);

        Assert.True(result.Success);

        // Verify artwork is cleared from file
        using (var reread = TagLib.File.Create(filePath))
        {
            Assert.Empty(reread.Tag.Pictures);
        }

        var dbAlbum = await _dbContext.GetAlbumByIdAsync(result.DbResult!.UpdatedTrack!.AlbumId);
        Assert.NotNull(dbAlbum);
        Assert.Null(dbAlbum.ArtworkUrl);
    }

    // =================================================================
    // 5. READ-ONLY FILE HANDLING
    // =================================================================

    [Fact]
    public async Task UpdateTrackMetadataAsync_ReadOnlyFile_FailsGracefullyWithoutMutating()
    {
        var (filePath, track) = await CreateTestTrackAsync("readonly.mp3");
        File.SetAttributes(filePath, FileAttributes.ReadOnly);

        try
        {
            var mockLibraryService = new Mock<ILibraryService>();
            var editor = new TrackMetadataEditor(_dbContext, mockLibraryService.Object);

            var update = new TrackMetadataUpdate("New Title", "New Artist", "New Album");
            var result = await editor.UpdateTrackMetadataAsync(track.Id, update);

            Assert.False(result.Success);
            Assert.False(result.FileResult.Success);
            Assert.Contains("Read-Only", result.FileResult.ErrorMessage);
        }
        finally
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
        }
    }

    // =================================================================
    // 6. FILE LOCK / IN-USE HANDLING
    // =================================================================

    [Fact]
    public async Task UpdateTrackMetadataAsync_LockedFile_DetectsLockAndReturnsFailure()
    {
        var (filePath, track) = await CreateTestTrackAsync("locked.mp3");

        // Lock file with exclusive read-write sharing
        using var lockStream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var mockLibraryService = new Mock<ILibraryService>();
        var editor = new TrackMetadataEditor(_dbContext, mockLibraryService.Object);

        var update = new TrackMetadataUpdate("Locked Title", "Locked Artist", "Locked Album");
        var result = await editor.UpdateTrackMetadataAsync(track.Id, update);

        Assert.False(result.Success);
        Assert.False(result.FileResult.Success);
        Assert.Contains("locked", result.FileResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // =================================================================
    // 7. MISSING TRACK ID IN DATABASE
    // =================================================================

    [Fact]
    public async Task UpdateTrackMetadataAsync_NonexistentTrack_ReturnsFileFailed()
    {
        var mockLibraryService = new Mock<ILibraryService>();
        var editor = new TrackMetadataEditor(_dbContext, mockLibraryService.Object);

        var update = new TrackMetadataUpdate("Title", "Artist", "Album");
        var result = await editor.UpdateTrackMetadataAsync("tr_does_not_exist_999", update);

        Assert.False(result.Success);
        Assert.False(result.FileResult.Success);
        Assert.Contains("not found", result.FileResult.ErrorMessage);
    }
}
