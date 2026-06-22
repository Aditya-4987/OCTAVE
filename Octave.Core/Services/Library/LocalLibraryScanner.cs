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

namespace Octave.Core.Services.Library;

public record LibraryScanProgressEventArgs(
    int TotalFilesFound,
    int FilesProcessed,
    string CurrentProcessingFile
);

public class LocalLibraryScanner
{
    private readonly SqliteDbContext _dbContext;

    public event EventHandler<LibraryScanProgressEventArgs>? ScanProgressChanged;

    public LocalLibraryScanner(SqliteDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task ScanAsync(string rootPath, CancellationToken ct)
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
                    string artistName = string.IsNullOrWhiteSpace(tagFile.Tag.FirstPerformer) ? "Unknown Artist" : tagFile.Tag.FirstPerformer;
                    string albumTitle = string.IsNullOrWhiteSpace(tagFile.Tag.Album) ? "Unknown Album" : tagFile.Tag.Album;
                    string artistId = IdGenerator.FromArtist(artistName);
                    string albumId = IdGenerator.FromAlbum(artistName, albumTitle);
                    string trackId = IdGenerator.FromTrackUri(filePath);
                    DateTime dateAdded = IdGenerator.ResolveFileDateAdded(filePath);

                    var artist = new Artist(artistId, artistName, null, null, true);
                    var album = new Album(albumId, albumTitle, artistId, artistName, (int)tagFile.Tag.Year, null, "Local");

                    string trackTitle = string.IsNullOrWhiteSpace(tagFile.Tag.Title)
                        ? Path.GetFileNameWithoutExtension(filePath)
                        : tagFile.Tag.Title;

                    var track = new Track(
                        trackId,
                        trackTitle,
                        artistId,
                        artistName,
                        albumId,
                        albumTitle,
                        tagFile.Properties.Duration.TotalSeconds,
                        filePath,
                        "Local",
                        (int)tagFile.Tag.Track,
                        (int)tagFile.Tag.Year,
                        dateAdded
                    );

                    await _dbContext.UpsertArtistAsync(artist, tx);
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

            // Commit the transaction
            await tx.CommitAsync(ct);

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
    }
}
