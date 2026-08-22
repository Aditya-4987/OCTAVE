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

        // 1. Check Two-Tier Cache
        var cached = await _cache.GetAsync<LyricsData>(cacheKey, ct).ConfigureAwait(false);
        if (cached != null && cached.State != LyricsState.Unavailable && cached.State != LyricsState.Loading)
        {
            return cached;
        }

        // 2. Deduplicate concurrent in-flight requests for the same track lyrics
        string inFlightKey = $"fetch_lyrics:{cacheKey}";
        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            // Re-check cache inside single-flight
            var cachedAgain = await _cache.GetAsync<LyricsData>(cacheKey, ct).ConfigureAwait(false);
            if (cachedAgain != null && cachedAgain.State != LyricsState.Unavailable && cachedAgain.State != LyricsState.Loading)
            {
                return cachedAgain;
            }

            var sortedProviders = _providers
                .Where(p => p.IsEnabled)
                .OrderBy(p => p.Priority)
                .ToList();

            foreach (var provider in sortedProviders)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var result = await provider.FetchLyricsAsync(title, artist, album, durationSeconds, externalIds, ct).ConfigureAwait(false);
                    if (result != null && (result.State == LyricsState.Synced || result.State == LyricsState.Unsynced))
                    {
                        var data = new LyricsData(result.TrackId, result.State, result.SyncedLines, result.PlainText);
                        // Cache successful lyrics retrieval with a 30-day TTL
                        await _cache.SetAsync(cacheKey, data, TimeSpan.FromDays(30), ct).ConfigureAwait(false);
                        return data;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[OnlineLyricsOrchestrator] Provider '{provider.ProviderName}' failed: {ex.Message}");
                }
            }

            // Fallback: Check stale cache on provider failure / offline
            var staleEntry = await _cache.GetWithMetadataAsync<LyricsData>(cacheKey, allowStale: true, ct).ConfigureAwait(false);
            if (staleEntry != null && staleEntry.Value.State != LyricsState.Unavailable && staleEntry.Value.State != LyricsState.Loading)
            {
                return staleEntry.Value;
            }

            return new LyricsData(null, LyricsState.Unavailable, null, null);
        }).ConfigureAwait(false) ?? new LyricsData(null, LyricsState.Unavailable, null, null);
    }
}
