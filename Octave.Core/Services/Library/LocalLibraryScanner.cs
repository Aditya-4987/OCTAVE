using System;
using System.Collections.Concurrent;
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

// TotalFilesFound carries the number of files the producer has enumerated so far.
// While enumeration is still running it reports -1 (indeterminate): the count keeps
// rising mid-scan, so a percentage derived from it would be misleading (SCAN-12).
// Consumers must treat a negative value as "no stable total yet".
public record LibraryScanProgressEventArgs(
    int TotalFilesFound,
    int FilesProcessed,
    string CurrentProcessingFile
);

public class LocalLibraryScanner : Octave.Core.Interfaces.ILibraryScanner
{
    // SCAN-01: DB writes are flushed in short transactions of this many prepared
    // tracks instead of holding one write transaction across the entire scan.
    private const int UpsertBatchSize = 200;

    private readonly SqliteDbContext _dbContext;
    private readonly IArtworkCacheManager _artworkCacheManager;
    private readonly System.Threading.SemaphoreSlim _writeSemaphore = new(1, 1);
    private readonly System.Collections.Generic.List<string> _monitoredPaths = new();
    // SCAN-05: keyed by folder, cleared at the start of every full scan so art added
    // to a folder after a previous scan is picked up again.
    private readonly ConcurrentDictionary<string, string?> _folderArtCache = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<LibraryScanProgressEventArgs>? ScanProgressChanged;
    public event EventHandler? LibraryChanged;

    public System.Collections.Generic.IReadOnlyList<string> MonitoredPaths
    {
        get
        {
            lock (_monitoredPaths)
            {
                return _monitoredPaths.ToArray();
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

    // One parsed-and-artworked entry waiting for its write batch. Tag parsing and
    // artwork extraction happen OUTSIDE any transaction (SCAN-01); only these
    // ready-to-write entities enter the short upsert batches.
    private sealed record PreparedTrack(System.Collections.Generic.List<Artist> Artists, Album Album, Track Track);

    private async Task ScanInternalAsync(string rootPath, CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("Root path cannot be empty", nameof(rootPath));

            // SCAN-05: a rescan must re-probe folders for art added since the last walk.
            _folderArtCache.Clear();

            // SCAN-08: reachability is verified before anything else. An absent root
            // can never justify deleting anything under it.
            bool rootReachable = Directory.Exists(rootPath);
            if (!rootReachable)
            {
                Debug.WriteLine($"[Scanner] Root '{rootPath}' is not reachable; skipping scan (reconciliation disabled for this run).");
                return;
            }

            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(100)
            {
                SingleWriter = true,
                SingleReader = true
            });

            int totalFilesFound = 0;
            int processedCount = 0;
            var discoveredUris = new ConcurrentBag<string>();
            // SCAN-08: set when any directory under the root could not be enumerated.
            // A partially-walked tree yields an incomplete discovered set, which must
            // never drive deletions.
            bool enumerationIncomplete = false;

            var batch = new System.Collections.Generic.List<PreparedTrack>(UpsertBatchSize);

            // Lane 1: The Producer Task (recursive directory walk).
            // SCAN-02: the producer owns a linked CTS cancelled by the consumer's
            // error path, so it can never block forever on a channel nobody drains.
            using (var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                CancellationToken producerCt = producerCts.Token;

                async Task WalkAsync(string directory)
                {
                    producerCt.ThrowIfCancellationRequested();

                    System.Collections.Generic.IEnumerable<string> files;
                    System.Collections.Generic.IEnumerable<string> subDirectories;
                    try
                    {
                        files = Directory.EnumerateFiles(directory);
                        subDirectories = Directory.EnumerateDirectories(directory);
                    }
                    // SCAN-08: unlike IgnoreInaccessible=true (which silently skipped
                    // unreadable folders), a failure here is RECORDED as incompleteness.
                    catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is DirectoryNotFoundException)
                    {
                        enumerationIncomplete = true;
                        Debug.WriteLine($"[Scanner] Skipping unreadable directory '{directory}': {ex.Message}");
                        return;
                    }

                    foreach (var filePath in files)
                    {
                        producerCt.ThrowIfCancellationRequested();

                        string ext = Path.GetExtension(filePath).ToLowerInvariant();
                        if (ext == ".flac" || ext == ".mp3" || ext == ".m4a" || ext == ".wav" ||
                            ext == ".wma" || ext == ".aac" || ext == ".ogg" || ext == ".opus")
                        {
                            Interlocked.Increment(ref totalFilesFound);
                            discoveredUris.Add(filePath);
                            await channel.Writer.WriteAsync(filePath, producerCt);
                        }
                    }

                    foreach (var sub in subDirectories)
                    {
                        await WalkAsync(sub);
                    }
                }

                var producerTask = Task.Run(async () =>
                {
                    try
                    {
                        await WalkAsync(rootPath);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Consumer-fault cancellation (SCAN-02): the consumer rethrows
                        // its own error; ours would only shadow it.
                    }
                    catch (Exception ex)
                    {
                        // An aborted walk means we cannot trust what we did discover.
                        enumerationIncomplete = true;
                        Debug.WriteLine($"[Scanner] Enumeration of '{rootPath}' ended early: {ex.Message}");
                    }
                    finally
                    {
                        channel.Writer.TryComplete();
                    }
                });

                // Lane 2: The Consumer Task — parse + artwork OUTSIDE any transaction
                // (SCAN-01), flushing ready entities to the DB in short ~200-track
                // transactions.
                Exception? consumerFailure = null;
                try
                {
                    await foreach (string filePath in channel.Reader.ReadAllAsync(ct))
                    {
                        ct.ThrowIfCancellationRequested();

                        TagLib.File? tagFile = null;
                        try
                        {
                            tagFile = TagLib.File.Create(filePath);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Scanner] Skipped corrupt file: {filePath} ({ex.Message})");
                        }

                        if (tagFile != null)
                        {
                            // SCAN-10: the whole processing block is guarded per track —
                            // one bad file (null Properties, failed artwork cache write,
                            // …) is logged and skipped instead of rolling back the scan.
                            try
                            {
                                batch.Add(await BuildPreparedTrackAsync(tagFile, filePath));
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                Debug.WriteLine($"[Scanner] Failed to process file '{filePath}': {ex.Message}");
                            }
                            finally
                            {
                                tagFile.Dispose();
                            }
                        }

                        processedCount++;

                        if (processedCount % 25 == 0)
                        {
                            ReportProgress(ref totalFilesFound, processedCount, filePath, channel);
                        }

                        if (batch.Count >= UpsertBatchSize)
                        {
                            await FlushBatchAsync(batch, ct);
                            batch.Clear();
                        }
                    }

                    // Await the producer thread to ensure it finished producing.
                    await producerTask;
                }
                catch (Exception ex)
                {
                    consumerFailure = ex;
                    // SCAN-02: unblock the producer (it may sit in WriteAsync on a full
                    // channel nobody drains anymore) and always reap it so no task leaks.
                    producerCts.Cancel();
                    try { await producerTask; }
                    catch { /* cancelled with us */ }
                }

                if (consumerFailure != null)
                    throw consumerFailure;

                // Flush whatever remains after the walk completed.
                await FlushBatchAsync(batch, ct);

                ReportProgress(ref totalFilesFound, processedCount, "Scan Completed", channel);

                // Database Reconciliation Phase — its OWN short transaction (SCAN-01),
                // gated on a fully-enumerated reachable root with at least one file
                // discovered (SCAN-08). An incomplete or empty discovery can never
                // justify deletions.
                if (!enumerationIncomplete && Volatile.Read(ref totalFilesFound) > 0)
                {
                    await ReconcileRootAsync(rootPath, discoveredUris, ct);
                }
                else
                {
                    Debug.WriteLine("[Scanner] Reconciliation skipped: the discovered set may be incomplete " +
                                    "(unreadable directories or zero files found). No tracks will be deleted.");
                }

                LibraryChanged?.Invoke(this, EventArgs.Empty);

                // SCAN-06: orphan-artwork deletion is a filesystem side effect — it
                // belongs AFTER every DB commit, never inside a transaction that could
                // roll back.
                await SweepOrphanArtworkAsync(ct);
            }
        });
    }

    private void ReportProgress(ref int totalFilesFound, int processedCount, string currentFile, Channel<string> channel)
    {
        // SCAN-12: -1 signals "enumeration still running" so consumers don't render a
        // percentage against a moving target.
        int denominator = channel.Reader.Completion.IsCompleted
            ? Volatile.Read(ref totalFilesFound)
            : -1;

        ScanProgressChanged?.Invoke(this, new LibraryScanProgressEventArgs(
            TotalFilesFound: denominator,
            FilesProcessed: processedCount,
            CurrentProcessingFile: currentFile
        ));
    }

    // SCAN-01: parse tags + extract artwork live OUTSIDE the write transaction; this
    // builds the ready-to-upsert entity graph for one file.
    private async Task<PreparedTrack> BuildPreparedTrackAsync(TagLib.File tagFile, string filePath)
    {
        var (individualArtists, displayArtistName, primaryArtistName, albumArtistName) = ParseArtistNames(tagFile);
        string albumTitle = string.IsNullOrWhiteSpace(tagFile.Tag.Album) ? "Unknown Album" : tagFile.Tag.Album;

        // SCAN-09: the ALBUM is keyed by its album artist, not the first track
        // performer — compilations stay one album and guest features no longer split
        // their parent album.
        string albumArtistId = IdGenerator.FromArtist(albumArtistName);
        string albumId = IdGenerator.FromAlbum(albumArtistName, albumTitle);

        string primaryArtistId = IdGenerator.FromArtist(primaryArtistName);
        string trackId = IdGenerator.FromTrackUri(filePath);
        DateTime dateAdded = IdGenerator.ResolveFileDateAdded(filePath);

        string? artworkUrl = await ExtractHighestQualityArtworkAsync(tagFile, filePath);

        var album = new Album(albumId, albumTitle, albumArtistId, albumArtistName, (int)tagFile.Tag.Year, artworkUrl);

        string trackTitle = string.IsNullOrWhiteSpace(tagFile.Tag.Title)
            ? Path.GetFileNameWithoutExtension(filePath)
            : tagFile.Tag.Title;

        string genre = tagFile.Tag.FirstGenre ?? "";
        float replayGain = ExtractReplayGain(tagFile);

        // SCAN-11: disc boundaries are persisted so album views can order
        // (Disc, TrackNumber); a missing/empty tag normalizes to disc 1.
        int discNumber = Math.Max(1, (int)tagFile.Tag.Disc);

        var track = new Track(
            trackId,
            trackTitle,
            primaryArtistId,
            displayArtistName,
            albumId,
            albumTitle,
            tagFile.Properties.Duration.TotalSeconds,
            filePath,
            (int)tagFile.Tag.Track,
            (int)tagFile.Tag.Year,
            dateAdded,
            genre,
            replayGain,
            discNumber
        );

        var artists = new System.Collections.Generic.List<Artist>();
        foreach (var aName in individualArtists)
        {
            artists.Add(new Artist(IdGenerator.FromArtist(aName), aName, null, null));
        }

        return new PreparedTrack(artists, album, track);
    }

    // SCAN-01: one short write transaction per batch of prepared entries.
    private async Task FlushBatchAsync(System.Collections.Generic.List<PreparedTrack> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        ct.ThrowIfCancellationRequested();

        var tx = await _dbContext.BeginTransactionAsync();
        var txConnection = tx.Connection;
        if (txConnection == null)
            throw new InvalidOperationException("Transaction connection is not open.");

        try
        {
            foreach (var prepared in batch)
            {
                foreach (var artist in prepared.Artists)
                {
                    await _dbContext.UpsertArtistAsync(artist, tx);
                }
                await _dbContext.UpsertAlbumAsync(prepared.Album, tx);
                await _dbContext.UpsertTrackAsync(prepared.Track, tx);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            tx.Dispose();
            txConnection.Dispose();
        }
    }

    // SCAN-01/04/08/13: runs in its own short transaction, only for roots that were
    // fully enumerated, comparing GetFullPath-normalized paths on BOTH sides.
    private async Task ReconcileRootAsync(string rootPath, ConcurrentBag<string> discoveredUris, CancellationToken ct)
    {
        var tx = await _dbContext.BeginTransactionAsync();
        var txConnection = tx.Connection;
        if (txConnection == null)
            throw new InvalidOperationException("Transaction connection is not open.");

        try
        {
            // NF-07: load only rows under THIS root instead of the whole library —
            // scanning N roots no longer costs N full-table loads.
            string normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            var discoveredSet = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var uri in discoveredUris)
            {
                try
                {
                    discoveredSet.Add(Path.GetFullPath(uri)); // SCAN-04: normalize the enumerated side too
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Scanner] Skipping un-normalizable discovered path '{uri}': {ex.Message}");
                }
            }

            var dbTracks = new System.Collections.Generic.List<(string Id, string SourceUri)>();
            using (var cmd = txConnection.CreateCommand())
            {
                cmd.Transaction = tx;
                // LIKE is case-insensitive for ASCII by default; a missed row can only
                // fail SAFE (kept, never deleted). ESCAPE handles %/_/\ in real paths.
                string likePattern = EscapeLikePrefix(normalizedRoot) + "%";
                cmd.CommandText = "SELECT Id, SourceUri FROM Tracks WHERE SourceUri LIKE @prefix ESCAPE '\\';";
                cmd.Parameters.AddWithValue("@prefix", likePattern);
                using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        dbTracks.Add((reader.GetString(0), reader.GetString(1)));
                    }
                }
            }

            foreach (var dbTrack in dbTracks)
            {
                ct.ThrowIfCancellationRequested();

                // SCAN-13: one malformed stored path must not roll back the scan.
                string normalizedTrackPath;
                try
                {
                    normalizedTrackPath = Path.GetFullPath(dbTrack.SourceUri);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Scanner] Skipping row with malformed path '{dbTrack.SourceUri}': {ex.Message}");
                    continue;
                }

                if (normalizedTrackPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                    !discoveredSet.Contains(normalizedTrackPath))
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

            using (var cleanCmd = txConnection.CreateCommand())
            {
                cleanCmd.Transaction = tx;
                cleanCmd.CommandText = "DELETE FROM Albums WHERE Id NOT IN (SELECT DISTINCT AlbumId FROM Tracks);";
                await cleanCmd.ExecuteNonQueryAsync(ct);

                cleanCmd.CommandText = "DELETE FROM Artists WHERE Id NOT IN (SELECT DISTINCT ArtistId FROM Albums UNION SELECT DISTINCT ArtistId FROM Tracks);";
                await cleanCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            tx.Dispose();
            txConnection?.Dispose();
        }
    }

    private static string EscapeLikePrefix(string prefix) =>
        prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // SCAN-06: non-fatal sweep of cached artwork no longer referenced by any row.
    // Runs strictly AFTER all commits.
    private async Task SweepOrphanArtworkAsync(CancellationToken ct)
    {
        try
        {
            string cacheRoot = _artworkCacheManager.CacheRoot;
            if (!string.IsNullOrWhiteSpace(cacheRoot) && Directory.Exists(cacheRoot))
            {
                var dbArtworkTokens = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var tx = await _dbContext.BeginTransactionAsync();
                var txConnection = tx.Connection;
                if (txConnection == null)
                    throw new InvalidOperationException("Transaction connection is not open.");
                try
                {
                    using (var cmd = txConnection.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "SELECT DISTINCT ArtworkUrl FROM Albums WHERE ArtworkUrl IS NOT NULL UNION SELECT DISTINCT ArtworkUrl FROM Artists WHERE ArtworkUrl IS NOT NULL;";
                        using (var reader = await cmd.ExecuteReaderAsync(ct))
                        {
                            while (await reader.ReadAsync(ct))
                            {
                                dbArtworkTokens.Add(reader.GetString(0));
                            }
                        }
                    }
                    await tx.CommitAsync(ct);
                }
                finally
                {
                    tx.Dispose();
                    txConnection.Dispose();
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Scanner] Non-fatal artwork cleanup sweep failure: {ex.Message}");
        }
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
                var prepared = await BuildPreparedTrackAsync(tagFile, path);

                foreach (var aEntity in prepared.Artists)
                {
                    await _dbContext.UpsertArtistAsync(aEntity, tx);
                }
                await _dbContext.UpsertAlbumAsync(prepared.Album, tx);
                await _dbContext.UpsertTrackAsync(prepared.Track, tx);

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

                    cmd.CommandText = "DELETE FROM Artists WHERE Id NOT IN (SELECT DISTINCT ArtistId FROM Albums UNION SELECT DISTINCT ArtistId FROM Tracks);";
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
                        // SCAN-10 companion hardening: one failing root must not abort
                        // the remaining roots' scans.
                        try
                        {
                            await ScanAsync(path, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Scanner] Reconciliation scan of '{path}' failed: {ex.Message}");
                        }
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

            // Fallback: Check track directory for high-res folder images (cached per folder)
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                if (_folderArtCache.TryGetValue(dir, out var cachedArt))
                {
                    return cachedArt;
                }

                if (Directory.Exists(dir))
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
                                string? cachedToken = await _artworkCacheManager.CacheBytesAsync(bytes, mime);
                                _folderArtCache[dir] = cachedToken;
                                return cachedToken;
                            }
                        }
                    }
                }

                _folderArtCache[dir] = null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Scanner] Non-fatal artwork extraction error on file '{filePath}': {ex.Message}");
        }

        return null;
    }

    public static (System.Collections.Generic.List<string> IndividualArtists, string DisplayArtistName, string PrimaryArtistName, string AlbumArtistName) ParseArtistNames(TagLib.File tagFile)
    {
        var rawArtists = new System.Collections.Generic.List<string>();

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

        var individualArtists = new System.Collections.Generic.List<string>();
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // SCAN-03: '/' and '\' are NO LONGER delimiters — "AC/DC" is one artist, not
        // two. ';' remains the explicit multi-value separator.
        char[] delimiters = { ';' };

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

        // SCAN-09: prefer the album-artist tag for album identity; fall back to the
        // first performer-derived artist exactly as before when the tag is absent.
        string albumArtistName =
            CleanSingleName(tagFile.Tag.FirstAlbumArtist) ??
            (tagFile.Tag.AlbumArtists is { Length: > 0 } ? CleanSingleName(tagFile.Tag.AlbumArtists[0]) : null) ??
            primaryArtist;

        return (individualArtists, displayArtist, primaryArtist, albumArtistName);
    }

    // Takes the first ';'-segment of a (possibly multi-value) tag field, trimmed.
    private static string? CleanSingleName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string first = value.Split(';')[0].Trim();
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }

    // ReplayGain extraction shared by the full-scan and single-file paths.
    private static float ExtractReplayGain(TagLib.File tagFile)
    {
        try
        {
            if (tagFile.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2)
            {
                var txxx = System.Linq.Enumerable.FirstOrDefault(id3v2.GetFrames<TagLib.Id3v2.UserTextInformationFrame>(), f => f.Description.Equals("REPLAYGAIN_TRACK_GAIN", StringComparison.OrdinalIgnoreCase));
                if (txxx != null && txxx.Text.Length > 0)
                {
                    string val = txxx.Text[0].Replace(" dB", "").Trim();
                    if (float.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float parsedRg))
                    {
                        return parsedRg;
                    }
                }
            }
            else if (tagFile.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                string[] rgComments = xiph.GetField("REPLAYGAIN_TRACK_GAIN");
                if (rgComments.Length > 0)
                {
                    string val = rgComments[0].Replace(" dB", "").Trim();
                    if (float.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float parsedRg2))
                    {
                        return parsedRg2;
                    }
                }
            }
        }
        catch { /* ReplayGain is best-effort metadata */ }

        return 0.0f;
    }
}
