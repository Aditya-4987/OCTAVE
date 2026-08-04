using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Octave.Core.Helpers;
using Octave.Core.Models;
using Octave.Core.Services.Database;
using Octave.Core.Interfaces;

namespace Octave.Core.Services.Library;

public record LibraryScanProgressEventArgs(
    int TotalFilesFound,
    int FilesProcessed,
    string CurrentProcessingFile
);

public class LocalLibraryScanner : Octave.Core.Interfaces.ILibraryScanner
{
    private readonly SqliteDbContext _dbContext;
    private readonly IArtworkCacheManager _artworkCacheManager;
    private readonly System.Threading.SemaphoreSlim _writeSemaphore = new(1, 1);
    private readonly System.Collections.Generic.List<string> _monitoredPaths = new();

    public event EventHandler<LibraryScanProgressEventArgs>? ScanProgressChanged;
    public event EventHandler? LibraryChanged;

    public System.Collections.Generic.IReadOnlyList<string> MonitoredPaths
    {
        get
        {
            lock (_monitoredPaths)
            {
                return _monitoredPaths.AsReadOnly();
            }
        }
    }

    public void AddMonitoredPath(string path)
    {
        lock (_monitoredPaths)
        {
            if (!_monitoredPaths.Contains(path))
            {
                _monitoredPaths.Add(path);
            }
        }
    }

    public void RemoveMonitoredPath(string path)
    {
        lock (_monitoredPaths)
        {
            _monitoredPaths.Remove(path);
        }
    }

    public LocalLibraryScanner(SqliteDbContext dbContext, IArtworkCacheManager artworkCacheManager)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _artworkCacheManager = artworkCacheManager ?? throw new ArgumentNullException(nameof(artworkCacheManager));
    }

    public async Task ScanAsync(string rootPath, CancellationToken ct)
    {
        await _writeSemaphore.WaitAsync(ct);
        try
        {
            await ScanInternalAsync(rootPath, ct);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    private async Task ScanInternalAsync(string rootPath, CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("Root path cannot be empty", nameof(rootPath));

            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(100)
            {
                SingleWriter = true,
                SingleReader = true
            });

            int totalFilesFound = 0;
            int processedCount = 0;
            var discoveredUris = new System.Collections.Concurrent.ConcurrentBag<string>();

            // Lane 1: The Producer Task (runs on a background thread)
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    var enumerationOptions = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true
                    };

                    foreach (var filePath in Directory.EnumerateFiles(rootPath, "*.*", enumerationOptions))
                    {
                        ct.ThrowIfCancellationRequested();
                        
                        string ext = Path.GetExtension(filePath).ToLowerInvariant();
                        if (ext == ".flac" || ext == ".mp3" || ext == ".m4a" || ext == ".wav" ||
                            ext == ".wma" || ext == ".aac" || ext == ".ogg" || ext == ".opus")
                        {
                            Interlocked.Increment(ref totalFilesFound);
                            discoveredUris.Add(filePath);
                            await channel.Writer.WriteAsync(filePath, ct);
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is DirectoryNotFoundException)
                {
                    // Silently swallow OS-level directory lockouts, unmapped network drops, or path-too-long faults
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct);

            // Lane 2: The Consumer Task (The Transaction Master)
            var tx = await _dbContext.BeginTransactionAsync();
            var txConnection = tx.Connection;
            if (txConnection == null)
                throw new InvalidOperationException("Transaction connection is not open.");

            try
            {
                await foreach (string filePath in channel.Reader.ReadAllAsync(ct))
                {
                    ct.ThrowIfCancellationRequested();

                    TagLib.File tagFile;
                    try
                    {
                        tagFile = TagLib.File.Create(filePath);
                    }
                    catch (Exception)
                    {
                        Debug.WriteLine($"[Scanner] Skipped corrupt file: {filePath}");
                        processedCount++;

                        if (processedCount % 25 == 0)
                        {
                            ScanProgressChanged?.Invoke(this, new LibraryScanProgressEventArgs(
                                TotalFilesFound: Volatile.Read(ref totalFilesFound),
                                FilesProcessed: processedCount,
                                CurrentProcessingFile: filePath
                            ));
                        }
                        continue;
                    }

                    try
                    {
                        var (individualArtists, displayArtistName, primaryArtistName) = ParseArtistNames(tagFile);
                        string albumTitle = string.IsNullOrWhiteSpace(tagFile.Tag.Album) ? "Unknown Album" : tagFile.Tag.Album;
                        string primaryArtistId = IdGenerator.FromArtist(primaryArtistName);
                        string albumId = IdGenerator.FromAlbum(primaryArtistName, albumTitle);
                        string trackId = IdGenerator.FromTrackUri(filePath);
                        DateTime dateAdded = IdGenerator.ResolveFileDateAdded(filePath);

                        string? artworkUrl = await ExtractHighestQualityArtworkAsync(tagFile, filePath);

                        var album = new Album(albumId, albumTitle, primaryArtistId, primaryArtistName, (int)tagFile.Tag.Year, artworkUrl, "Local");

                        string trackTitle = string.IsNullOrWhiteSpace(tagFile.Tag.Title)
                            ? Path.GetFileNameWithoutExtension(filePath)
                            : tagFile.Tag.Title;

                        string genre = tagFile.Tag.FirstGenre ?? "";

                        double replayGain = 0.0;
                        try {
                            if (tagFile.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2) {
                                var txxx = System.Linq.Enumerable.FirstOrDefault(id3v2.GetFrames<TagLib.Id3v2.UserTextInformationFrame>(), f => f.Description.Equals("REPLAYGAIN_TRACK_GAIN", StringComparison.OrdinalIgnoreCase));
                                if (txxx != null && txxx.Text.Length > 0) {
                                    string val = txxx.Text[0].Replace(" dB", "").Trim();
                                    if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedRg)) {
                                        replayGain = parsedRg;
                                    }
                                }
                            } else if (tagFile.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph) {
                                string[] rgComments = xiph.GetField("REPLAYGAIN_TRACK_GAIN");
                                if (rgComments.Length > 0) {
                                    string val = rgComments[0].Replace(" dB", "").Trim();
                                    if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedRg)) {
                                        replayGain = parsedRg;
                                    }
                                }
                            }
                        } catch { }

                        var track = new Track(
                            trackId,
                            trackTitle,
                            primaryArtistId,
                            displayArtistName,
                            albumId,
                            albumTitle,
                            tagFile.Properties.Duration.TotalSeconds,
                            filePath,
                            "Local",
                            (int)tagFile.Tag.Track,
                            (int)tagFile.Tag.Year,
                            dateAdded,
                            genre,
                            replayGain
                        );

                        foreach (var aName in individualArtists)
                        {
                            string aId = IdGenerator.FromArtist(aName);
                            var aEntity = new Artist(aId, aName, null, null, true);
                            await _dbContext.UpsertArtistAsync(aEntity, tx);
                        }

                        await _dbContext.UpsertAlbumAsync(album, tx);
                        await _dbContext.UpsertTrackAsync(track, tx);

                        processedCount++;

                        if (processedCount % 25 == 0)
                        {
                            ScanProgressChanged?.Invoke(this, new LibraryScanProgressEventArgs(
                                TotalFilesFound: Volatile.Read(ref totalFilesFound),
                                FilesProcessed: processedCount,
                                CurrentProcessingFile: filePath
                            ));
                        }
                    }
                    finally
                    {
                        tagFile.Dispose();
                    }
                }

                // Await the producer thread to ensure it finished producing
                await producerTask;

                // Database Reconciliation Phase
                var dbTracks = new List<(string Id, string SourceUri)>();
                using (var cmd = txConnection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT Id, SourceUri FROM Tracks WHERE Provider = 'Local';";
                    using (var reader = await cmd.ExecuteReaderAsync(ct))
                    {
                        while (await reader.ReadAsync(ct))
                        {
                            dbTracks.Add((reader.GetString(0), reader.GetString(1)));
                        }
                    }
                }

                string normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var discoveredSet = new HashSet<string>(discoveredUris, StringComparer.OrdinalIgnoreCase);

                foreach (var dbTrack in dbTracks)
                {
                    ct.ThrowIfCancellationRequested();
                    
                    string normalizedTrackPath = Path.GetFullPath(dbTrack.SourceUri);
                    if (normalizedTrackPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!discoveredSet.Contains(dbTrack.SourceUri))
                        {
                            using (var deleteCmd = txConnection.CreateCommand())
                            {
                                deleteCmd.Transaction = tx;
                                deleteCmd.CommandText = "DELETE FROM Tracks WHERE Id = @id;";
                                deleteCmd.Parameters.AddWithValue("@id", dbTrack.Id);
                                await deleteCmd.ExecuteNonQueryAsync(ct);
                            }
                        }
                    }
                }

                using (var cleanCmd = txConnection.CreateCommand())
                {
                    cleanCmd.Transaction = tx;
                    cleanCmd.CommandText = "DELETE FROM Albums WHERE Id NOT IN (SELECT DISTINCT AlbumId FROM Tracks);";
                    await cleanCmd.ExecuteNonQueryAsync(ct);

                    cleanCmd.CommandText = "DELETE FROM Artists WHERE Id NOT IN (SELECT DISTINCT ArtistId FROM Albums);";
                    await cleanCmd.ExecuteNonQueryAsync(ct);
                }

                // Artwork Cleanup Sweep
                try
                {
                    string cacheRoot = _artworkCacheManager.CacheRoot;
                    if (!string.IsNullOrWhiteSpace(cacheRoot) && Directory.Exists(cacheRoot))
                    {
                        var dbArtworkTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        using (var cmd = txConnection.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "SELECT DISTINCT ArtworkUrl FROM Albums WHERE ArtworkUrl IS NOT NULL;";
                            using (var reader = await cmd.ExecuteReaderAsync(ct))
                            {
                                while (await reader.ReadAsync(ct))
                                {
                                    dbArtworkTokens.Add(reader.GetString(0));
                                }
                            }
                        }

                        var physicalFiles = Directory.GetFiles(cacheRoot, "*.*");
                        if (physicalFiles != null && physicalFiles.Length > 0)
                        {
                            foreach (var filePath in physicalFiles)
                            {
                                ct.ThrowIfCancellationRequested();
                                string fileName = Path.GetFileName(filePath);
                                string relativeToken = $"ArtworkCache/{fileName}";
                                if (!dbArtworkTokens.Contains(relativeToken))
                                {
                                    try
                                    {
                                        File.Delete(filePath);
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"[Scanner] Failed to delete orphan artwork file '{filePath}': {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Scanner] Non-fatal artwork cleanup sweep failure: {ex.Message}");
                }

                // Commit the transaction
                await tx.CommitAsync(ct);
                LibraryChanged?.Invoke(this, EventArgs.Empty);

                // Final progress update if any files were scanned
                if (processedCount > 0)
                {
                    ScanProgressChanged?.Invoke(this, new LibraryScanProgressEventArgs(
                        TotalFilesFound: Volatile.Read(ref totalFilesFound),
                        FilesProcessed: processedCount,
                        CurrentProcessingFile: "Scan Completed"
                    ));
                }
            }
            catch (Exception)
            {
                await tx.RollbackAsync(ct);
                throw;
            }
            finally
            {
                tx.Dispose();
                txConnection?.Dispose();
            }
        });
    }

    public async Task ScanFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty", nameof(path));

        await _writeSemaphore.WaitAsync();
        try
        {
            if (!File.Exists(path))
                return;

            TagLib.File tagFile;
            try
            {
                tagFile = TagLib.File.Create(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Scanner] Skipped corrupt file: {path}. Error: {ex.Message}");
                return;
            }

            var tx = await _dbContext.BeginTransactionAsync();
            var txConnection = tx.Connection;
            if (txConnection == null)
                throw new InvalidOperationException("Transaction connection is not open.");

            try
            {
                var (individualArtists, displayArtistName, primaryArtistName) = ParseArtistNames(tagFile);
                string albumTitle = string.IsNullOrWhiteSpace(tagFile.Tag.Album) ? "Unknown Album" : tagFile.Tag.Album;
                string primaryArtistId = IdGenerator.FromArtist(primaryArtistName);
                string albumId = IdGenerator.FromAlbum(primaryArtistName, albumTitle);
                string trackId = IdGenerator.FromTrackUri(path);
                DateTime dateAdded = IdGenerator.ResolveFileDateAdded(path);

                string? artworkUrl = await ExtractHighestQualityArtworkAsync(tagFile, path);

                var album = new Album(albumId, albumTitle, primaryArtistId, primaryArtistName, (int)tagFile.Tag.Year, artworkUrl, "Local");

                string trackTitle = string.IsNullOrWhiteSpace(tagFile.Tag.Title)
                    ? Path.GetFileNameWithoutExtension(path)
                    : tagFile.Tag.Title;

                string genre = tagFile.Tag.FirstGenre ?? "";

                double replayGain = 0.0;
                try {
                    if (tagFile.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2) {
                        var txxx = System.Linq.Enumerable.FirstOrDefault(id3v2.GetFrames<TagLib.Id3v2.UserTextInformationFrame>(), f => f.Description.Equals("REPLAYGAIN_TRACK_GAIN", StringComparison.OrdinalIgnoreCase));
                        if (txxx != null && txxx.Text.Length > 0) {
                            string val = txxx.Text[0].Replace(" dB", "").Trim();
                            if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedRg)) {
                                replayGain = parsedRg;
                            }
                        }
                    } else if (tagFile.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph) {
                        string[] rgComments = xiph.GetField("REPLAYGAIN_TRACK_GAIN");
                        if (rgComments.Length > 0) {
                            string val = rgComments[0].Replace(" dB", "").Trim();
                            if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedRg)) {
                                replayGain = parsedRg;
                            }
                        }
                    }
                } catch { }

                var track = new Track(
                    trackId,
                    trackTitle,
                    primaryArtistId,
                    displayArtistName,
                    albumId,
                    albumTitle,
                    tagFile.Properties.Duration.TotalSeconds,
                    path,
                    "Local",
                    (int)tagFile.Tag.Track,
                    (int)tagFile.Tag.Year,
                    dateAdded,
                    genre,
                    replayGain
                );

                foreach (var aName in individualArtists)
                {
                    string aId = IdGenerator.FromArtist(aName);
                    var aEntity = new Artist(aId, aName, null, null, true);
                    await _dbContext.UpsertArtistAsync(aEntity, tx);
                }

                await _dbContext.UpsertAlbumAsync(album, tx);
                await _dbContext.UpsertTrackAsync(track, tx);

                await tx.CommitAsync();
                LibraryChanged?.Invoke(this, EventArgs.Empty);
                
                Debug.WriteLine($"[LibraryWatcher] Incremental scan completed: {path}");

                ScanProgressChanged?.Invoke(this, new LibraryScanProgressEventArgs(
                    TotalFilesFound: 1,
                    FilesProcessed: 1,
                    CurrentProcessingFile: path
                ));
            }
            catch (Exception)
            {
                await tx.RollbackAsync();
                throw;
            }
            finally
            {
                tagFile.Dispose();
                tx.Dispose();
                txConnection?.Dispose();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task RemoveStalePathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty", nameof(path));

        await _writeSemaphore.WaitAsync();
        try
        {
            var tx = await _dbContext.BeginTransactionAsync();
            var txConnection = tx.Connection;
            if (txConnection == null)
                throw new InvalidOperationException("Transaction connection is not open.");

            try
            {
                using (var cmd = txConnection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM Tracks WHERE SourceUri = @path;";
                    cmd.Parameters.AddWithValue("@path", path);
                    await cmd.ExecuteNonQueryAsync();

                    cmd.Parameters.Clear();
                    cmd.CommandText = "DELETE FROM Albums WHERE Id NOT IN (SELECT DISTINCT AlbumId FROM Tracks);";
                    await cmd.ExecuteNonQueryAsync();

                    cmd.CommandText = "DELETE FROM Artists WHERE Id NOT IN (SELECT DISTINCT ArtistId FROM Albums);";
                    await cmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                LibraryChanged?.Invoke(this, EventArgs.Empty);

                ScanProgressChanged?.Invoke(this, new LibraryScanProgressEventArgs(
                    TotalFilesFound: 0,
                    FilesProcessed: 0,
                    CurrentProcessingFile: $"Removed: {path}"
                ));
            }
            catch (Exception)
            {
                await tx.RollbackAsync();
                throw;
            }
            finally
            {
                tx.Dispose();
                txConnection?.Dispose();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    private Task? _activeReconciliationTask = null;
    private readonly object _reconciliationLock = new();
    private DateTime _lastReconciliationTime = DateTime.MinValue;

    public Task RequestFullReconciliationAsync()
    {
        lock (_reconciliationLock)
        {
            if (_activeReconciliationTask != null)
            {
                return _activeReconciliationTask;
            }

            _activeReconciliationTask = Task.Run(async () =>
            {
                try
                {
                    TimeSpan elapsed = DateTime.UtcNow - _lastReconciliationTime;
                    TimeSpan minInterval = TimeSpan.FromSeconds(30);
                    if (elapsed < minInterval)
                    {
                        TimeSpan delay = minInterval - elapsed;
                        await Task.Delay(delay);
                    }

                    foreach (var path in MonitoredPaths)
                    {
                        await ScanAsync(path, CancellationToken.None);
                    }

                    _lastReconciliationTime = DateTime.UtcNow;
                }
                finally
                {
                    lock (_reconciliationLock)
                    {
                        _activeReconciliationTask = null;
                    }
                }
            });

            return _activeReconciliationTask;
        }
    }

    private async Task<string?> ExtractHighestQualityArtworkAsync(TagLib.File tagFile, string filePath)
    {
        try
        {
            if (tagFile.Tag.Pictures != null && tagFile.Tag.Pictures.Length > 0)
            {
                // 1. Prefer FrontCover frame if available with valid byte data
                var picture = System.Linq.Enumerable.FirstOrDefault(tagFile.Tag.Pictures, p => p.Type == TagLib.PictureType.FrontCover && p.Data?.Data != null && p.Data.Data.Length > 0);

                // 2. Otherwise pick the picture with the largest data payload (highest resolution)
                if (picture == null)
                {
                    picture = System.Linq.Enumerable.OrderByDescending(tagFile.Tag.Pictures, p => p.Data?.Data?.Length ?? 0).FirstOrDefault();
                }

                if (picture?.Data?.Data != null && picture.Data.Data.Length > 512)
                {
                    return await _artworkCacheManager.CacheBytesAsync(picture.Data.Data, picture.MimeType);
                }
            }

            // Fallback: Check track directory for high-res folder images
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                string[] candidateNames = { "cover.jpg", "cover.png", "folder.jpg", "folder.png", "album.jpg", "album.png", "front.jpg", "front.png" };
                foreach (var name in candidateNames)
                {
                    string candidatePath = Path.Combine(dir, name);
                    if (File.Exists(candidatePath))
                    {
                        byte[] bytes = await File.ReadAllBytesAsync(candidatePath);
                        if (bytes.Length > 512)
                        {
                            string mime = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                            return await _artworkCacheManager.CacheBytesAsync(bytes, mime);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Scanner] Non-fatal artwork extraction error on file '{filePath}': {ex.Message}");
        }

        return null;
    }

    public static (List<string> IndividualArtists, string DisplayArtistName, string PrimaryArtistName) ParseArtistNames(TagLib.File tagFile)
    {
        var rawArtists = new List<string>();

        if (tagFile.Tag.Performers != null && tagFile.Tag.Performers.Length > 0)
        {
            rawArtists.AddRange(tagFile.Tag.Performers);
        }
        if (tagFile.Tag.AlbumArtists != null && tagFile.Tag.AlbumArtists.Length > 0)
        {
            rawArtists.AddRange(tagFile.Tag.AlbumArtists);
        }

        if (rawArtists.Count == 0 && !string.IsNullOrWhiteSpace(tagFile.Tag.FirstPerformer))
        {
            rawArtists.Add(tagFile.Tag.FirstPerformer);
        }

        var individualArtists = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        char[] delimiters = new char[] { ';', '/', '\\' };

        foreach (var raw in rawArtists)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string[] parts = raw.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                string cleaned = part.Trim();
                if (string.IsNullOrWhiteSpace(cleaned)) continue;

                string[] featParts = System.Text.RegularExpressions.Regex.Split(cleaned, @"\s+(?:feat\.|ft\.|featuring)\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var fPart in featParts)
                {
                    string sub = fPart.Trim();
                    if (!string.IsNullOrWhiteSpace(sub) && seen.Add(sub))
                    {
                        individualArtists.Add(sub);
                    }
                }
            }
        }

        if (individualArtists.Count == 0)
        {
            individualArtists.Add("Unknown Artist");
        }

        string primaryArtist = individualArtists[0];
        string displayArtist = string.Join("; ", individualArtists);

        return (individualArtists, displayArtist, primaryArtist);
    }
}
