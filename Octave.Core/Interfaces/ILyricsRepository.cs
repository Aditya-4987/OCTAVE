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
    DateTimeOffset LastCheckedAt
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
        CancellationToken cancellationToken = default);

    Task DeleteCachedLyricsAsync(string trackId, CancellationToken cancellationToken = default);
}
