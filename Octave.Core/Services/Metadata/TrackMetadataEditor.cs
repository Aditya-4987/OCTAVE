using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Database;
using Octave.Core.Services.Library;

namespace Octave.Core.Services.Metadata;

public class TrackMetadataEditor : ITrackMetadataEditor
{
    private readonly SqliteDbContext _dbContext;
    private readonly ILibraryService _libraryService;
    private readonly IArtworkCacheManager? _artworkCacheManager;

    public TrackMetadataEditor(
        SqliteDbContext dbContext,
        ILibraryService libraryService,
        IArtworkCacheManager? artworkCacheManager = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _artworkCacheManager = artworkCacheManager;
    }

    public async Task<MetadataEditResult> UpdateTrackMetadataAsync(
        string trackId,
        TrackMetadataUpdate update,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return MetadataEditResult.FileFailed(FileWriteResult.Failed("", "Track ID cannot be null or empty."));

        if (update == null)
            return MetadataEditResult.FileFailed(FileWriteResult.Failed("", "Metadata update payload cannot be null."));

        // 1. Locate track in SQLite database
        var existingTrack = await _dbContext.GetTrackByIdAsync(trackId).ConfigureAwait(false);
        if (existingTrack == null)
        {
            return MetadataEditResult.FileFailed(FileWriteResult.Failed("", $"Track with ID '{trackId}' was not found in library."));
        }

        string filePath = existingTrack.SourceUri;
        if (!File.Exists(filePath))
        {
            return MetadataEditResult.FileFailed(FileWriteResult.Failed(filePath, $"Audio file not found on disk at '{filePath}'."));
        }

        // 2. Check Read-Only attributes
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.IsReadOnly)
        {
            return MetadataEditResult.FileFailed(FileWriteResult.Failed(filePath, "Audio file is marked as Read-Only."));
        }

        // 3. Check for file locks by other processes
        try
        {
            using (var lockCheck = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // File is accessible and unlocked
            }
        }
        catch (IOException ioEx)
        {
            return MetadataEditResult.FileFailed(FileWriteResult.Failed(filePath, $"Audio file is locked or currently in use: {ioEx.Message}"));
        }

        if (ct.IsCancellationRequested)
        {
            return MetadataEditResult.FileFailed(FileWriteResult.Failed(filePath, "Operation was cancelled."));
        }

        // =================================================================
        // STAGE 1: Safe File Tag Writing via TagLib# with Rollback Sandbox
        // =================================================================
        string backupPath = filePath + ".octave_bak";
        FileWriteResult fileResult;

        try
        {
            // Create backup to guarantee rollback on partial/corrupted write
            File.Copy(filePath, backupPath, true);

            await Task.Run(() =>
            {
                using (var tagFile = TagLib.File.Create(filePath))
                {
                    // 1. Basic Metadata
                    if (update.Title != null)
                        tagFile.Tag.Title = update.Title.Trim();

                    if (update.ArtistName != null)
                        tagFile.Tag.Performers = new[] { update.ArtistName.Trim() };

                    if (update.AlbumTitle != null)
                        tagFile.Tag.Album = update.AlbumTitle.Trim();

                    if (update.AlbumArtist != null)
                        tagFile.Tag.AlbumArtists = new[] { update.AlbumArtist.Trim() };

                    if (update.Composer != null)
                        tagFile.Tag.Composers = new[] { update.Composer.Trim() };

                    if (update.Genre != null)
                        tagFile.Tag.Genres = new[] { update.Genre.Trim() };

                    if (update.Year.HasValue)
                        tagFile.Tag.Year = update.Year.Value > 0 ? (uint)update.Year.Value : 0;

                    if (update.TrackNumber.HasValue)
                        tagFile.Tag.Track = update.TrackNumber.Value > 0 ? (uint)update.TrackNumber.Value : 0;

                    if (update.TrackCount.HasValue)
                        tagFile.Tag.TrackCount = update.TrackCount.Value > 0 ? (uint)update.TrackCount.Value : 0;

                    if (update.DiscNumber.HasValue)
                        tagFile.Tag.Disc = update.DiscNumber.Value > 0 ? (uint)update.DiscNumber.Value : 0;

                    if (update.DiscCount.HasValue)
                        tagFile.Tag.DiscCount = update.DiscCount.Value > 0 ? (uint)update.DiscCount.Value : 0;

                    if (update.Comment != null)
                        tagFile.Tag.Comment = update.Comment;

                    if (update.Lyrics != null)
                        tagFile.Tag.Lyrics = update.Lyrics;

                    // 2. MusicBrainz Identifiers
                    if (update.ExternalIds != null)
                    {
                        if (!string.IsNullOrWhiteSpace(update.ExternalIds.MusicBrainzId))
                            tagFile.Tag.MusicBrainzTrackId = update.ExternalIds.MusicBrainzId;

                        string? relId = update.ExternalIds.GetId("MusicBrainzReleaseId");
                        if (!string.IsNullOrWhiteSpace(relId))
                            tagFile.Tag.MusicBrainzReleaseId = relId;

                        string? artId = update.ExternalIds.GetId("MusicBrainzArtistId");
                        if (!string.IsNullOrWhiteSpace(artId))
                            tagFile.Tag.MusicBrainzArtistId = artId;

                        string? rgId = update.ExternalIds.GetId("MusicBrainzReleaseGroupId");
                        if (!string.IsNullOrWhiteSpace(rgId))
                            tagFile.Tag.MusicBrainzReleaseGroupId = rgId;

                        if (!string.IsNullOrWhiteSpace(update.ExternalIds.Isrc))
                            tagFile.Tag.ISRC = update.ExternalIds.Isrc;
                    }

                    // 3. Embedded Artwork Handling
                    if (update.ClearArtwork)
                    {
                        tagFile.Tag.Pictures = Array.Empty<TagLib.IPicture>();
                    }
                    else if (update.NewArtworkBytes != null && update.NewArtworkBytes.Length > 0)
                    {
                        var picture = new TagLib.Picture(new TagLib.ByteVector(update.NewArtworkBytes))
                        {
                            Type = TagLib.PictureType.FrontCover,
                            MimeType = update.ArtworkMimeType ?? "image/jpeg",
                            Description = "Front Cover"
                        };
                        tagFile.Tag.Pictures = new TagLib.IPicture[] { picture };
                    }
                    // Else: existing artwork is strictly preserved!

                    tagFile.Save();
                }
            }, ct).ConfigureAwait(false);

            // Write succeeded: remove backup
            if (File.Exists(backupPath))
            {
                try { File.Delete(backupPath); } catch { }
            }

            fileResult = FileWriteResult.Succeeded(filePath);
        }
        catch (Exception ex)
        {
            // Restore from backup to guarantee original file remains intact
            if (File.Exists(backupPath))
            {
                try
                {
                    File.Copy(backupPath, filePath, true);
                    File.Delete(backupPath);
                }
                catch { }
            }

            fileResult = FileWriteResult.Failed(filePath, $"Failed to write audio tags: {ex.Message}");
            return MetadataEditResult.FileFailed(fileResult);
        }

        // =================================================================
        // STAGE 2: Reread Actual File Tags from Disk for Verification
        // =================================================================
        string verifiedTitle = update.Title ?? existingTrack.Title;
        string verifiedArtist = update.ArtistName ?? existingTrack.ArtistName;
        string verifiedAlbum = update.AlbumTitle ?? existingTrack.AlbumTitle;
        string verifiedGenre = update.Genre ?? existingTrack.Genre;
        int verifiedYear = update.Year ?? existingTrack.Year;
        int verifiedTrackNumber = update.TrackNumber ?? existingTrack.TrackNumber;
        double verifiedDuration = existingTrack.DurationSeconds;
        byte[]? verifiedArtworkBytes = null;
        string? verifiedArtworkMime = null;

        try
        {
            using (var rereadFile = TagLib.File.Create(filePath))
            {
                if (!string.IsNullOrWhiteSpace(rereadFile.Tag.Title))
                    verifiedTitle = rereadFile.Tag.Title.Trim();

                if (rereadFile.Tag.Performers != null && rereadFile.Tag.Performers.Length > 0 && !string.IsNullOrWhiteSpace(rereadFile.Tag.Performers[0]))
                    verifiedArtist = rereadFile.Tag.Performers[0].Trim();

                if (!string.IsNullOrWhiteSpace(rereadFile.Tag.Album))
                    verifiedAlbum = rereadFile.Tag.Album.Trim();

                if (rereadFile.Tag.Genres != null && rereadFile.Tag.Genres.Length > 0 && !string.IsNullOrWhiteSpace(rereadFile.Tag.Genres[0]))
                    verifiedGenre = rereadFile.Tag.Genres[0].Trim();

                if (rereadFile.Tag.Year > 0)
                    verifiedYear = (int)rereadFile.Tag.Year;

                if (rereadFile.Tag.Track > 0)
                    verifiedTrackNumber = (int)rereadFile.Tag.Track;

                if (rereadFile.Properties != null && rereadFile.Properties.Duration.TotalSeconds > 0)
                    verifiedDuration = rereadFile.Properties.Duration.TotalSeconds;

                if (rereadFile.Tag.Pictures != null && rereadFile.Tag.Pictures.Length > 0)
                {
                    var front = rereadFile.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? rereadFile.Tag.Pictures[0];
                    if (front.Data != null && front.Data.Data != null && front.Data.Data.Length > 0)
                    {
                        verifiedArtworkBytes = front.Data.Data;
                        verifiedArtworkMime = front.MimeType;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrackMetadataEditor] Warning: Failed to reread file after write: {ex.Message}");
        }

        // =================================================================
        // STAGE 3: Artwork Cache & Database Synchronization
        // =================================================================
        DbSyncResult dbResult;
        try
        {
            string artistId = IdGenerator.FromArtist(verifiedArtist);
            string albumId = IdGenerator.FromAlbum(verifiedArtist, verifiedAlbum);

            string? artworkUri = null;
            if (_artworkCacheManager != null)
            {
                if (update.ClearArtwork)
                {
                    artworkUri = null;
                }
                else if (verifiedArtworkBytes != null && verifiedArtworkBytes.Length > 0)
                {
                    artworkUri = await _artworkCacheManager.CacheBytesAsync(verifiedArtworkBytes, verifiedArtworkMime).ConfigureAwait(false);
                }
                else
                {
                    // Preserve existing album artwork if not cleared
                    var existingAlbum = await _dbContext.GetAlbumByIdAsync(existingTrack.AlbumId).ConfigureAwait(false);
                    artworkUri = existingAlbum?.ArtworkUrl;
                }
            }

            var artistRecord = new Artist(artistId, verifiedArtist, null, null, true);
            var albumRecord = new Album(albumId, verifiedAlbum, artistId, verifiedArtist, verifiedYear, artworkUri, "Local");

            await _dbContext.UpsertArtistAsync(artistRecord).ConfigureAwait(false);
            await _dbContext.UpsertAlbumAsync(albumRecord).ConfigureAwait(false);

            float replayGain = update.ReplayGain ?? existingTrack.ReplayGain;

            var updatedTrack = new Track(
                existingTrack.Id,
                verifiedTitle,
                artistId,
                verifiedArtist,
                albumId,
                verifiedAlbum,
                verifiedDuration,
                existingTrack.SourceUri,
                existingTrack.Provider,
                verifiedTrackNumber,
                verifiedYear,
                existingTrack.DateAdded,
                verifiedGenre,
                replayGain
            );

            await _dbContext.UpsertTrackAsync(updatedTrack).ConfigureAwait(false);
            dbResult = DbSyncResult.Succeeded(updatedTrack);
        }
        catch (Exception ex)
        {
            dbResult = DbSyncResult.Failed($"Database synchronization failed: {ex.Message}");
            return MetadataEditResult.DbSyncFailed(fileResult, dbResult);
        }

        return MetadataEditResult.Succeeded(fileResult, dbResult);
    }
}
