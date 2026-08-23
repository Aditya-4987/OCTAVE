using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Cache;

namespace Octave.Core.Services.External;

public class OnlineLyricsOrchestrator : IOnlineLyricsOrchestrator
{
    // ORC-01: a sweep that finds nothing is remembered briefly so a track with
    // no lyrics anywhere doesn't re-hit every provider on each UI refresh
    // (offline sessions especially). Far shorter than the 30-day positive TTL,
    // it lets new providers/data pick the track up later.
    private static readonly TimeSpan NegativeResultTtl = TimeSpan.FromHours(6);

    private readonly IEnumerable<IExternalLyricsProvider> _providers;
    private readonly IExternalDataCache _cache;
    private readonly AsyncSingleFlight _singleFlight = new();

    public OnlineLyricsOrchestrator(
        IEnumerable<IExternalLyricsProvider> providers,
        IExternalDataCache cache)
    {
        _providers = providers ?? Array.Empty<IExternalLyricsProvider>();
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<LyricsData> FetchLyricsAsync(
        string title,
        string artist,
        string? album = null,
        double? durationSeconds = null,
        ExternalIds? externalIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return new LyricsData(null, LyricsState.Unavailable, null, null);
        }

        string normArtist = MetadataTextNormalizer.Normalize(artist);
        string normTitle = MetadataTextNormalizer.Normalize(title);
        string cacheKey = $"lyrics:{normArtist}:{normTitle}";

        // 1. Check Two-Tier Cache. A fresh Unavailable entry is ORC-01's negative
        // sentinel — serve it instead of re-sweeping the providers. Stale
        // entries of any state fall through to a live attempt; Loading never
        // caches.
        var cachedItem = await _cache.GetWithMetadataAsync<LyricsData>(cacheKey, allowStale: false, ct).ConfigureAwait(false);
        if (cachedItem != null && !cachedItem.IsExpired &&
            cachedItem.Value.State is LyricsState.Synced or LyricsState.Unsynced or LyricsState.Unavailable)
        {
            return cachedItem.Value;
        }

        // 2. Deduplicate concurrent in-flight requests for the same track lyrics
        string inFlightKey = $"fetch_lyrics:{cacheKey}";
        try
        {
            return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
            {
                // Re-check cache inside single-flight
                var cachedAgain = await _cache.GetWithMetadataAsync<LyricsData>(cacheKey).ConfigureAwait(false);
                if (cachedAgain != null && !cachedAgain.IsExpired &&
                    cachedAgain.Value.State is LyricsState.Synced or LyricsState.Unsynced or LyricsState.Unavailable)
                {
                    return cachedAgain.Value;
                }

                var sortedProviders = _providers
                    .Where(p => p.IsEnabled)
                    .OrderBy(p => p.Priority)
                    .ToList();

                // SF-01: this factory runs detached from any single caller's token —
                // AsyncSingleFlight applies each caller's ct to their personal wait
                // only. The sweep completes under the providers' own timeouts and its
                // result lands in the cache for everyone.
                foreach (var provider in sortedProviders)
                {
                    try
                    {
                        var result = await provider.FetchLyricsAsync(title, artist, album, durationSeconds, externalIds, CancellationToken.None).ConfigureAwait(false);
                        if (result != null && (result.State == LyricsState.Synced || result.State == LyricsState.Unsynced))
                        {
                            var data = new LyricsData(result.TrackId, result.State, result.SyncedLines, result.PlainText);
                            // Cache successful lyrics retrieval with a 30-day TTL
                            await _cache.SetAsync(cacheKey, data, TimeSpan.FromDays(30)).ConfigureAwait(false);
                            return data;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Provider-internal cancellation (its own timeouts) — treat as
                        // a provider failure and continue with the next one.
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[OnlineLyricsOrchestrator] Provider '{provider.ProviderName}' failed: {ex.Message}");
                    }
                }

                // Fallback: Check stale cache on provider failure / offline. A stale
                // NEGATIVE entry must not masquerade as a usable fallback.
                var staleEntry = await _cache.GetWithMetadataAsync<LyricsData>(cacheKey, allowStale: true).ConfigureAwait(false);
                if (staleEntry != null && staleEntry.Value.State != LyricsState.Unavailable && staleEntry.Value.State != LyricsState.Loading)
                {
                    return staleEntry.Value;
                }

                // ORC-01: remember the failed sweep as a short-TTL negative sentinel.
                var negative = new LyricsData(null, LyricsState.Unavailable, null, null);
                await _cache.SetAsync(cacheKey, negative, NegativeResultTtl).ConfigureAwait(false);
                return negative;
            }, ct).ConfigureAwait(false) ?? new LyricsData(null, LyricsState.Unavailable, null, null);
        }
        catch (OperationCanceledException)
        {
            // SF-01: only THIS caller's personal wait was cancelled — the shared
            // fetch continues untouched for everyone else. This request exits
            // gracefully (established orchestrator contract).
            return new LyricsData(null, LyricsState.Unavailable, null, null);
        }
    }
}
