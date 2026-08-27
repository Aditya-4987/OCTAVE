using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Interfaces;

public interface ILyricsService
{
    /// <summary>
    /// Fast Phase 1: Resolves lyrics immediately from local file (embedded tags, .lrc sidecars) and SQLite cache.
    /// Performs zero network requests and returns within milliseconds.
    /// </summary>
    Task<LyricsData> GetLocalAndCachedLyricsAsync(Track track, CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 2 background enrichment: Queries LRCLIB for any format that is missing in <paramref name="currentLyrics"/>,
    /// caches the resulting pair, and preserves existing local formats.
    /// </summary>
    Task<LyricsData> EnrichLyricsAsync(Track track, LyricsData currentLyrics, CancellationToken cancellationToken = default);

    /// <summary>
    /// Full resolution pipeline: executes Phase 1 followed by Phase 2 if any format is missing.
    /// </summary>
    Task<LyricsData> GetLyricsAsync(Track track, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces a refresh from remote LRCLIB provider: invalidates existing SQLite cache,
    /// queries LRCLIB remotely (Two-Stage: /api/get -> /api/search + scoring), replaces SQLite cache,
    /// and never modifies local audio file tags.
    /// </summary>
    Task<LyricsData> ReloadLyricsAsync(Track track, CancellationToken cancellationToken = default);
}

