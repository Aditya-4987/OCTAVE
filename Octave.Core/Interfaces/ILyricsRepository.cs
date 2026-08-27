using System;
using System.Threading;
using System.Threading.Tasks;

namespace Octave.Core.Interfaces;

public record CachedLyricsEntity(
    string TrackId,
    string? PlainLyrics,
    string? SyncedLyrics,
    bool HasPlainLyrics,
    bool HasSyncedLyrics,
    bool IsNotFound,
    DateTimeOffset CachedAt,
    DateTimeOffset LastCheckedAt,
    string? Source = null,
    long? LrclibRecordId = null,
    string? SyncedSource = null,
    string? StaticSource = null
);

public interface ILyricsRepository
{
    Task<CachedLyricsEntity?> GetCachedLyricsAsync(string trackId, CancellationToken cancellationToken = default);
    
    Task UpsertCachedLyricsAsync(
        string trackId,
        string? plainLyrics,
        string? syncedLyrics,
        bool hasPlainLyrics,
        bool hasSyncedLyrics,
        bool isNotFound,
        string? source,
        long? lrclibRecordId,
        string? syncedSource,
        string? staticSource,
        CancellationToken cancellationToken = default);

    Task UpsertCachedLyricsAsync(
        string trackId,
        string? plainLyrics,
        string? syncedLyrics,
        bool hasPlainLyrics,
        bool hasSyncedLyrics,
        bool isNotFound,
        string? source,
        long? lrclibRecordId,
        CancellationToken cancellationToken = default)
        => UpsertCachedLyricsAsync(trackId, plainLyrics, syncedLyrics, hasPlainLyrics, hasSyncedLyrics, isNotFound, source, lrclibRecordId, null, null, cancellationToken);

    Task UpsertCachedLyricsAsync(
        string trackId,
        string? plainLyrics,
        string? syncedLyrics,
        bool hasPlainLyrics,
        bool hasSyncedLyrics,
        bool isNotFound,
        string? source = null,
        CancellationToken cancellationToken = default)
        => UpsertCachedLyricsAsync(trackId, plainLyrics, syncedLyrics, hasPlainLyrics, hasSyncedLyrics, isNotFound, source, null, null, null, cancellationToken);

    Task DeleteCachedLyricsAsync(string trackId, CancellationToken cancellationToken = default);
}

