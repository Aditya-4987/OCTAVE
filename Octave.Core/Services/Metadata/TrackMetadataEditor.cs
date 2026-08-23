using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    // EDITOR-04: atomic-write sandbox artifacts. Both live NEXT TO the target
    // file because File.Replace is only atomic within one volume (ME-01 asked
    // for a temp-dir backup, but a cross-volume copy cannot be swapped
    // atomically - the same-dir temp + replace design supersedes it).
    private const string TempFileMarker = ".octave_tmp";
    private const string BackupFileSuffix = ".octave_bak";

    // TagLib# sniffs the file type from its extension, so the sandbox copy must
    // KEEP the real audio extension - the marker goes before it
    // ("song.octave_tmp.mp3"), not appended after ("song.mp3.octave_tmp",
    // which TagLib refuses to open as an unknown type).
    private static string BuildTempPath(string filePath)
    {
        string dir = Path.GetDirectoryName(filePath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(filePath);
        string ext = Path.GetExtension(filePath);
        return Path.Combine(dir, name + TempFileMarker + ext);
    }

    private readonly SqliteDbContext _dbContext;
    private readonly ILibraryService _libraryService;
    private readonly IArtworkCacheManager? _artworkCacheManager;

    // EDITOR-01/ME-03: multi-value frames split on unambiguous separators only -
    // ';' anywhere, '/' only when space-padded. "AC/DC" and "K/S" stay intact;
    // "Dido / Eminem" and "A; B" become separate frame values.
    private static readonly Regex MultiValueSplit = new(@"\s*;\s*|\s+/\s+", RegexOptions.Compiled);

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

        // 3. EDITOR-05/CRIT-03: the old exclusive-open lock probe is GONE. It
        // opened + closed a handle before the real write - a TOCTOU window that
        // proved nothing. Locks now surface naturally from the write itself and
        // are classified in the catch below.

        if (ct.IsCancellationRequested)
        {
            return MetadataEditResult.FileFailed(FileWriteResult.Failed(filePath, "Operation was cancelled."));
        }

        // =================================================================
        // STAGE 1: Safe File Tag Writing via TagLib# - atomic replace sandbox
        // =================================================================
        // EDITOR-04: edits run on a same-directory temp copy which is then
        // swapped over the original with File.Replace (atomic on NTFS). A crash
        // mid-write can no longer leave a half-written music file: either the
        // swap happened or the original is byte-for-byte untouched. The backup
        // argument receives the previous version during the swap itself.
        string tempPath = BuildTempPath(filePath);
        string backupPath = filePath + BackupFileSuffix;

        FileWriteResult fileResult;

        try
        {
            SafeDelete(tempPath); // stale artifact from a previous crash for THIS file

            await Task.Run(() =>
            {
                File.Copy(filePath, tempPath, overwrite: true);

                using (var tagFile = TagLib.File.Create(tempPath))
                {
                    // 1. Basic Metadata
                    if (update.Title != null)
                        tagFile.Tag.Title = update.Title.Trim();

                    // EDITOR-01/ME-03: split multi-value input into separate frame
                    // elements instead of collapsing everything into one; skip the
                    // write when the new frame is semantically identical to the
                    // existing one.
                    var performers = SplitMultiValue(update.ArtistName);
                    if (performers != null)
                        SetFrameIfDifferent(tagFile.Tag.Performers, performers, v => tagFile.Tag.Performers = v);

                    if (update.AlbumTitle != null)
                        tagFile.Tag.Album = update.AlbumTitle.Trim();

                    var albumArtists = SplitMultiValue(update.AlbumArtist);
                    if (albumArtists != null)
                        SetFrameIfDifferent(tagFile.Tag.AlbumArtists, albumArtists, v => tagFile.Tag.AlbumArtists = v);

                    var composers = SplitMultiValue(update.Composer);
                    if (composers != null)
                        SetFrameIfDifferent(tagFile.Tag.Composers, composers, v => tagFile.Tag.Composers = v);

                    var genres = SplitMultiValue(update.Genre);
                    if (genres != null)
                        SetFrameIfDifferent(tagFile.Tag.Genres, genres, v => tagFile.Tag.Genres = v);

                    // EDITOR-06/ME-04: out-of-range values are IGNORED (the existing
                    // tag is preserved) instead of written or erased. The old code
                    // mapped Year=-5 to 0 - actively erasing the year.
                    if (update.Year.HasValue && MetadataValueClamps.IsValidYear(update.Year.Value))
                        tagFile.Tag.Year = (uint)update.Year.Value;

                    if (update.TrackNumber.HasValue && MetadataValueClamps.IsValidTrackNumber(update.TrackNumber.Value))
                        tagFile.Tag.Track = (uint)update.TrackNumber.Value;

                    if (update.TrackCount.HasValue && MetadataValueClamps.IsValidTrackNumber(update.TrackCount.Value))
                        tagFile.Tag.TrackCount = (uint)update.TrackCount.Value;

                    if (update.DiscNumber.HasValue && MetadataValueClamps.IsValidTrackNumber(update.DiscNumber.Value))
                        tagFile.Tag.Disc = (uint)update.DiscNumber.Value;

                    if (update.DiscCount.HasValue && MetadataValueClamps.IsValidTrackNumber(update.DiscCount.Value))
                        tagFile.Tag.DiscCount = (uint)update.DiscCount.Value;

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

                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath); // stale from ancient non-atomic runs
                }

                // Atomic swap: original -> backupPath, tempPath -> original.
                // Throws (IOException/UnauthorizedAccessException) if the original
                // is locked or the directory unwritable - the original stays
                // intact in that case.
                File.Replace(tempPath, filePath, backupPath);
            }, ct).ConfigureAwait(false);

            // Write succeeded and the OS verified the swap: drop the backup.
            SafeDelete(backupPath);

            fileResult = FileWriteResult.Succeeded(filePath);
        }
        catch (Exception ex)
        {
            SafeDelete(tempPath);

            // EDITOR-07: if a backup exists at this point the replace itself
            // failed after swapping (rare). Restore from it, and delete the
            // backup only after the restore succeeded; otherwise return a hard
            // error NAMING the backup path so the user can recover manually.
            if (File.Exists(backupPath))
            {
                try
                {
                    File.Copy(backupPath, filePath, overwrite: true);
                    SafeDelete(backupPath);
                }
                catch (Exception restoreEx)
                {
                    return MetadataEditResult.FileFailed(FileWriteResult.Failed(filePath,
                        $"Failed to write audio tags, and restoring the original from '{backupPath}' also failed. The original file is preserved there: {restoreEx.Message}"));
                }
            }

            string message = ex switch
            {
                OperationCanceledException => "Operation was cancelled.",
                IOException ioEx => $"Audio file is locked or currently in use: {ioEx.Message}",
                UnauthorizedAccessException uaEx => $"Audio file access denied: {uaEx.Message}",
                _ => $"Failed to write audio tags: {ex.Message}"
            };

            fileResult = FileWriteResult.Failed(filePath, message);
            return MetadataEditResult.FileFailed(fileResult);
        }

        // =================================================================
        // STAGE 2: Reread Actual File Tags from Disk for Verification
        // =================================================================
        // ME-02: the fallbacks below are the write payload (update ?? existing) -
        // when the re-read itself fails, that payload IS the known on-disk state,
        // so the DB cannot diverge from the file. The re-read exists to pick up
        // TagLib normalization, not to gate the DB write.
        string verifiedTitle = update.Title ?? existingTrack.Title;
        string verifiedArtist = update.ArtistName ?? existingTrack.ArtistName;
        string verifiedAlbum = update.AlbumTitle ?? existingTrack.AlbumTitle;
        string verifiedGenre = update.Genre ?? existingTrack.Genre;
        // EDITOR-06: the fallbacks mirror the write gates exactly - a value the
        // file rejected never reaches the DB either.
        int verifiedYear = update.Year.HasValue && MetadataValueClamps.IsValidYear(update.Year.Value)
            ? update.Year.Value : existingTrack.Year;
        int verifiedTrackNumber = update.TrackNumber.HasValue && MetadataValueClamps.IsValidTrackNumber(update.TrackNumber.Value)
            ? update.TrackNumber.Value : existingTrack.TrackNumber;
        double verifiedDuration = existingTrack.DurationSeconds;
        byte[]? verifiedArtworkBytes = null;
        string? verifiedArtworkMime = null;

        // EDITOR-08: only re-read (and re-cache) artwork when this edit actually
        // touched the embedded art; otherwise the existing cached album art is
        // preserved downstream without paying full-picture read cost.
        bool artworkChanged = update.ClearArtwork ||
                              (update.NewArtworkBytes != null && update.NewArtworkBytes.Length > 0);

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

                if (artworkChanged && rereadFile.Tag.Pictures != null && rereadFile.Tag.Pictures.Length > 0)
                {
                    // Prefer the FrontCover frame; validate the bytes rather than
                    // trusting the frame's self-declared MIME.
                    var front = rereadFile.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? rereadFile.Tag.Pictures[0];
                    if (front.Data != null && front.Data.Data != null && front.Data.Data.Length > 0 &&
                        ImageValidator.IsValidImage(front.Data.Data, out string detectedMime))
                    {
                        verifiedArtworkBytes = front.Data.Data;
                        verifiedArtworkMime = detectedMime;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrackMetadataEditor] Warning: Failed to reread file after write: {ex.Message}");
        }

        // =================================================================
        // STAGE 3: Artwork Cache & Database Synchronization (one transaction)
        // =================================================================
        DbSyncResult dbResult;
        try
        {
            string artistId = IdGenerator.FromArtist(verifiedArtist);
            string albumId = IdGenerator.FromAlbum(verifiedArtist, verifiedAlbum);
            string oldArtistId = existingTrack.ArtistId;
            string oldAlbumId = existingTrack.AlbumId;

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
                    // Preserve existing album artwork if not cleared (reads the OLD
                    // album row, so a rename carries its art over to the new id).
                    var existingAlbum = await _dbContext.GetAlbumByIdAsync(oldAlbumId).ConfigureAwait(false);
                    artworkUri = existingAlbum?.ArtworkUrl;
                }
            }

            var artistRecord = new Artist(artistId, verifiedArtist, null, null, true);
            var albumRecord = new Album(albumId, verifiedAlbum, artistId, verifiedArtist, verifiedYear, artworkUri, "Local");

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

            // EDITOR-03: the rename upserts run in ONE transaction (cf. DB-04) so
            // a mid-failure can't leave a partial graph.
            await using (var tx = await _dbContext.BeginTransactionAsync().ConfigureAwait(false))
            {
                await _dbContext.UpsertArtistAsync(artistRecord, tx).ConfigureAwait(false);
                await _dbContext.UpsertAlbumAsync(albumRecord, tx).ConfigureAwait(false);
                await _dbContext.UpsertTrackAsync(updatedTrack, tx).ConfigureAwait(false);

                // EDITOR-02/ME-05: rows orphaned by the rename are deleted in the
                // SAME transaction - ghost artists/albums no longer linger until a
                // full rescan.
                await _dbContext.DeleteOrphanedArtistsAndAlbumsAsync(
                    new[] { oldArtistId }, new[] { oldAlbumId }, tx).ConfigureAwait(false);

                await tx.CommitAsync().ConfigureAwait(false);
            }

            dbResult = DbSyncResult.Succeeded(updatedTrack);
        }
        catch (Exception ex)
        {
            // EDITOR-09: the FILE write succeeded; the DB could not follow. This
            // is a real divergence (until the next scan) - log it prominently and
            // surface it to the user via DbSyncFailed.
            System.Diagnostics.Debug.WriteLine(
                $"[TrackMetadataEditor] FILE/DB DIVERGENCE: tags were written to '{filePath}' but the database sync failed: {ex}");

            dbResult = DbSyncResult.Failed($"Database synchronization failed: {ex.Message}");
            return MetadataEditResult.DbSyncFailed(fileResult, dbResult);
        }

        return MetadataEditResult.Succeeded(fileResult, dbResult);
    }

    // EDITOR-04: startup/crash recovery sweep. A stray .octave_bak can only
    // exist AFTER a successful atomic replace (it holds the replaced previous
    // version), and a stray .octave_tmp means the replace never ran (the
    // original is intact) - both are always safe to delete.
    public static int SweepStaleEditorArtifacts(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return 0;

        int removed = 0;
        try
        {
            // Temp artifacts keep their audio extension ("song.octave_tmp.mp3"),
            // hence the two temp patterns (with and without a trailing part).
            foreach (var pattern in new[] { "*" + TempFileMarker + ".*", "*" + TempFileMarker, "*" + BackupFileSuffix })
            {
                foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch
                    {
                        // In use or permission-restricted: leave it for the next sweep.
                    }
                }
            }
        }
        catch
        {
            // Root vanished mid-enumeration - nothing to sweep.
        }

        return removed;
    }

    private static string[]? SplitMultiValue(string? raw)
    {
        if (raw == null) return null;

        string[] parts = MultiValueSplit.Split(raw.Trim())
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        return parts.Length > 0 ? parts : null;
    }

    // Replaces the frame only when the incoming values differ semantically
    // (case-insensitive, order-sensitive) from the current ones.
    private static void SetFrameIfDifferent(string[]? current, string[] incoming, Action<string[]> assign)
    {
        if (incoming == null || incoming.Length == 0) return;

        if (current != null && current.Length == incoming.Length)
        {
            bool identical = true;
            for (int i = 0; i < current.Length; i++)
            {
                if (!string.Equals(current[i]?.Trim(), incoming[i], StringComparison.OrdinalIgnoreCase))
                {
                    identical = false;
                    break;
                }
            }

            if (identical) return;
        }

        assign(incoming);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; a leftover artifact is swept later.
        }
    }
}
