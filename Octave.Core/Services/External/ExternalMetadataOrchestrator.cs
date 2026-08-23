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

public class ExternalMetadataOrchestrator : IExternalMetadataOrchestrator
{
    // ORC-01: an empty search result is remembered briefly so a lookup with no
    // matches anywhere doesn't re-sweep every provider on repeat views. Far
    // shorter than the 7-day positive TTL by design.
    private static readonly TimeSpan NegativeResultTtl = TimeSpan.FromHours(1);

    private readonly IEnumerable<IExternalMetadataProvider> _providers;
    private readonly IExternalDataCache _cache;
    private readonly AsyncSingleFlight _singleFlight = new();

    public ExternalMetadataOrchestrator(
        IEnumerable<IExternalMetadataProvider> providers,
        IExternalDataCache cache)
    {
        _providers = providers ?? Array.Empty<IExternalMetadataProvider>();
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<IReadOnlyList<TrackMatchCandidate>> SearchTrackCandidatesAsync(
        string title,
        string artist,
        string? album = null,
        double? durationSeconds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return Array.Empty<TrackMatchCandidate>();
        }

        string normTitle = MetadataTextNormalizer.Normalize(title);
        string normArtist = MetadataTextNormalizer.Normalize(artist);
        string normAlbum = !string.IsNullOrWhiteSpace(album) ? MetadataTextNormalizer.Normalize(album) : "";
        string cacheKey = $"search:track:{normArtist}:{normTitle}:{normAlbum}";

        // 1. Check Cache. An EMPTY entry (only ever written as ORC-01's negative
        // sentinel) is served while fresh instead of re-sweeping the providers.
        var cachedItem = await _cache.GetWithMetadataAsync<List<TrackMatchCandidate>>(cacheKey, allowStale: false, ct).ConfigureAwait(false);
        if (cachedItem != null && !cachedItem.IsExpired && cachedItem.Value.Count > 0)
        {
            return cachedItem.Value;
        }
        bool negativeHit = cachedItem != null && !cachedItem.IsExpired;

        // 2. Single-flight request across providers
        string inFlightKey = $"search_tracks:{cacheKey}";
        try
        {
            return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
            {
                var cachedAgain = await _cache.GetAsync<List<TrackMatchCandidate>>(cacheKey).ConfigureAwait(false);
                if (cachedAgain != null && cachedAgain.Count > 0) return cachedAgain;
                if (negativeHit) return cachedAgain ?? new List<TrackMatchCandidate>();

                var results = new List<TrackMatchCandidate>();
                var sortedProviders = _providers.Where(p => p.IsEnabled).OrderBy(p => p.Priority);

                // SF-01: the shared sweep runs detached from any single caller's
                // token; AsyncSingleFlight applies each caller's ct to their own wait.
                foreach (var provider in sortedProviders)
                {
                    try
                    {
                        var candidates = await provider.SearchTrackCandidatesAsync(title, artist, album, durationSeconds, CancellationToken.None).ConfigureAwait(false);
                        if (candidates != null && candidates.Count > 0)
                        {
                            results.AddRange(candidates);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ExternalMetadataOrchestrator] Provider '{provider.ProviderName}' search failed: {ex.Message}");
                    }
                }

                // OL-06: different providers routinely report the SAME recording —
                // merge to one candidate per real-world track before ranking.
                var sorted = DedupeByIdentity(results.OrderByDescending(c => c.Confidence)).ToList();
                if (sorted.Count > 0)
                {
                    // Cache search candidates with 7-day TTL
                    await _cache.SetAsync(cacheKey, sorted, TimeSpan.FromDays(7)).ConfigureAwait(false);
                    return (IReadOnlyList<TrackMatchCandidate>)sorted;
                }

                // Fallback: Check for stale cached results on provider failure/offline
                var staleEntry = await _cache.GetWithMetadataAsync<List<TrackMatchCandidate>>(cacheKey, allowStale: true).ConfigureAwait(false);
                if (staleEntry != null && staleEntry.Value.Count > 0)
                {
                    return staleEntry.Value;
                }

                // ORC-01: remember the fruitless sweep briefly.
                var negative = new List<TrackMatchCandidate>();
                await _cache.SetAsync(cacheKey, negative, NegativeResultTtl).ConfigureAwait(false);
                return (IReadOnlyList<TrackMatchCandidate>)negative;
            }, ct).ConfigureAwait(false) ?? Array.Empty<TrackMatchCandidate>();
        }
        catch (OperationCanceledException)
        {
            // SF-01: only THIS caller's personal wait was cancelled — the shared
            // sweep continues untouched for everyone else. This request exits
            // gracefully (established orchestrator contract).
            return Array.Empty<TrackMatchCandidate>();
        }
    }

    public async Task<IReadOnlyList<AlbumMatchCandidate>> SearchAlbumCandidatesAsync(
        string albumTitle,
        string artistName,
        int? year = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(albumTitle) || string.IsNullOrWhiteSpace(artistName))
        {
            return Array.Empty<AlbumMatchCandidate>();
        }

        string normAlbum = MetadataTextNormalizer.Normalize(albumTitle);
        string normArtist = MetadataTextNormalizer.Normalize(artistName);
        string cacheKey = $"search:album:{normArtist}:{normAlbum}:{year ?? 0}";

        var cachedItem = await _cache.GetWithMetadataAsync<List<AlbumMatchCandidate>>(cacheKey, allowStale: false, ct).ConfigureAwait(false);
        if (cachedItem != null && !cachedItem.IsExpired && cachedItem.Value.Count > 0)
        {
            return cachedItem.Value;
        }
        bool negativeHit = cachedItem != null && !cachedItem.IsExpired;

        string inFlightKey = $"search_albums:{cacheKey}";
        try
        {
            return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
            {
                var cachedAgain = await _cache.GetAsync<List<AlbumMatchCandidate>>(cacheKey).ConfigureAwait(false);
                if (cachedAgain != null && cachedAgain.Count > 0) return cachedAgain;
                if (negativeHit) return cachedAgain ?? new List<AlbumMatchCandidate>();

                var results = new List<AlbumMatchCandidate>();
                var sortedProviders = _providers.Where(p => p.IsEnabled).OrderBy(p => p.Priority);

                foreach (var provider in sortedProviders)
                {
                    try
                    {
                        var candidates = await provider.SearchAlbumCandidatesAsync(albumTitle, artistName, year, CancellationToken.None).ConfigureAwait(false);
                        if (candidates != null && candidates.Count > 0)
                        {
                            results.AddRange(candidates);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ExternalMetadataOrchestrator] Provider '{provider.ProviderName}' album search failed: {ex.Message}");
                    }
                }

                var sorted = results.OrderByDescending(c => c.Confidence).ToList();
                if (sorted.Count > 0)
                {
                    await _cache.SetAsync(cacheKey, sorted, TimeSpan.FromDays(7)).ConfigureAwait(false);
                    return (IReadOnlyList<AlbumMatchCandidate>)sorted;
                }

                var staleEntry = await _cache.GetWithMetadataAsync<List<AlbumMatchCandidate>>(cacheKey, allowStale: true).ConfigureAwait(false);
                if (staleEntry != null && staleEntry.Value.Count > 0)
                {
                    return staleEntry.Value;
                }

                var negative = new List<AlbumMatchCandidate>();
                await _cache.SetAsync(cacheKey, negative, NegativeResultTtl).ConfigureAwait(false);
                return (IReadOnlyList<AlbumMatchCandidate>)negative;
            }, ct).ConfigureAwait(false) ?? Array.Empty<AlbumMatchCandidate>();
        }
        catch (OperationCanceledException)
        {
            // SF-01: only THIS caller's personal wait was cancelled — the shared
            // sweep continues untouched for everyone else. Exit gracefully.
            return Array.Empty<AlbumMatchCandidate>();
        }
    }

    public async Task<IReadOnlyList<ArtistMatchCandidate>> SearchArtistCandidatesAsync(
        string artistName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return Array.Empty<ArtistMatchCandidate>();
        }

        string normArtist = MetadataTextNormalizer.Normalize(artistName);
        string cacheKey = $"search:artist:{normArtist}";

        var cachedItem = await _cache.GetWithMetadataAsync<List<ArtistMatchCandidate>>(cacheKey, allowStale: false, ct).ConfigureAwait(false);
        if (cachedItem != null && !cachedItem.IsExpired && cachedItem.Value.Count > 0)
        {
            return cachedItem.Value;
        }
        bool negativeHit = cachedItem != null && !cachedItem.IsExpired;

        string inFlightKey = $"search_artists:{cacheKey}";
        try
        {
            return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
            {
                var cachedAgain = await _cache.GetAsync<List<ArtistMatchCandidate>>(cacheKey).ConfigureAwait(false);
                if (cachedAgain != null && cachedAgain.Count > 0) return cachedAgain;
                if (negativeHit) return cachedAgain ?? new List<ArtistMatchCandidate>();

                var results = new List<ArtistMatchCandidate>();
                var sortedProviders = _providers.Where(p => p.IsEnabled).OrderBy(p => p.Priority);

                foreach (var provider in sortedProviders)
                {
                    try
                    {
                        var candidates = await provider.SearchArtistCandidatesAsync(artistName, CancellationToken.None).ConfigureAwait(false);
                        if (candidates != null && candidates.Count > 0)
                        {
                            results.AddRange(candidates);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ExternalMetadataOrchestrator] Provider '{provider.ProviderName}' artist search failed: {ex.Message}");
                    }
                }

                var sorted = results.OrderByDescending(c => c.Confidence).ToList();
                if (sorted.Count > 0)
                {
                    await _cache.SetAsync(cacheKey, sorted, TimeSpan.FromDays(7)).ConfigureAwait(false);
                    return (IReadOnlyList<ArtistMatchCandidate>)sorted;
                }

                var staleEntry = await _cache.GetWithMetadataAsync<List<ArtistMatchCandidate>>(cacheKey, allowStale: true).ConfigureAwait(false);
                if (staleEntry != null && staleEntry.Value.Count > 0)
                {
                    return staleEntry.Value;
                }

                var negative = new List<ArtistMatchCandidate>();
                await _cache.SetAsync(cacheKey, negative, NegativeResultTtl).ConfigureAwait(false);
                return (IReadOnlyList<ArtistMatchCandidate>)negative;
            }, ct).ConfigureAwait(false) ?? Array.Empty<ArtistMatchCandidate>();
        }
        catch (OperationCanceledException)
        {
            // SF-01: only THIS caller's personal wait was cancelled — the shared
            // sweep continues untouched for everyone else. Exit gracefully.
            return Array.Empty<ArtistMatchCandidate>();
        }
    }

    // OL-06: one candidate per real-world recording. A MusicBrainz id, when
    // present, is authoritative; otherwise fall back to the normalized
    // title/artist/album triple so differently-keyed providers still collapse.
    private static IEnumerable<TrackMatchCandidate> DedupeByIdentity(IEnumerable<TrackMatchCandidate> ranked)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in ranked)
        {
            if (seen.Add(CandidateIdentityKey(candidate)))
            {
                yield return candidate;
            }
        }
    }

    private static string CandidateIdentityKey(TrackMatchCandidate c)
    {
        if (!string.IsNullOrWhiteSpace(c.ExternalIds.MusicBrainzId))
        {
            return "mbid:" + c.ExternalIds.MusicBrainzId.Trim();
        }

        var m = c.Metadata;
        return $"t:{m.Title?.Trim().ToLowerInvariant()}|a:{m.ArtistName?.Trim().ToLowerInvariant()}" +
               $"|al:{m.AlbumTitle?.Trim().ToLowerInvariant()}";
    }

    public async Task<ExternalTrackMetadata?> GetTrackMetadataAsync(
        string providerName,
        string providerEntityId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(providerEntityId))
            return null;

        string cacheKey = $"meta:track:{providerName.ToLowerInvariant()}:{providerEntityId}";
        var cached = await _cache.GetAsync<ExternalTrackMetadata>(cacheKey, ct).ConfigureAwait(false);
        if (cached != null) return cached;

        string inFlightKey = $"get_track:{cacheKey}";
        try
        {
            return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
            {
                var cachedAgain = await _cache.GetAsync<ExternalTrackMetadata>(cacheKey).ConfigureAwait(false);
                if (cachedAgain != null) return cachedAgain;

                var provider = _providers.FirstOrDefault(p => p.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) && p.IsEnabled);
                if (provider == null) return null;

                // SF-01: the shared fetch runs free of any single caller's token.
                try
                {
                    var meta = await provider.GetTrackMetadataAsync(providerEntityId, CancellationToken.None).ConfigureAwait(false);
                    if (meta != null)
                    {
                        await _cache.SetAsync(cacheKey, meta, TimeSpan.FromDays(30), CancellationToken.None).ConfigureAwait(false);
                        return meta;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExternalMetadataOrchestrator] GetTrackMetadataAsync failed for '{providerName}:{providerEntityId}': {ex.Message}");
                }

                // Stale fallback on network error/offline
                var stale = await _cache.GetWithMetadataAsync<ExternalTrackMetadata>(cacheKey, allowStale: true).ConfigureAwait(false);
                return stale?.Value;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // SF-01: only THIS caller's personal wait was cancelled — the shared
            // fetch continues untouched for everyone else. Exit gracefully.
            return null;
        }
    }

    public async Task<ExternalAlbumMetadata?> GetAlbumMetadataAsync(
        string providerName,
        string providerEntityId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(providerEntityId))
            return null;

        string cacheKey = $"meta:album:{providerName.ToLowerInvariant()}:{providerEntityId}";
        var cached = await _cache.GetAsync<ExternalAlbumMetadata>(cacheKey, ct).ConfigureAwait(false);
        if (cached != null) return cached;

        string inFlightKey = $"get_album:{cacheKey}";
        try
        {
            return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
            {
                var cachedAgain = await _cache.GetAsync<ExternalAlbumMetadata>(cacheKey).ConfigureAwait(false);
                if (cachedAgain != null) return cachedAgain;

                var provider = _providers.FirstOrDefault(p => p.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) && p.IsEnabled);
                if (provider == null) return null;

                // SF-01: the shared fetch runs free of any single caller's token.
                try
                {
                    var meta = await provider.GetAlbumMetadataAsync(providerEntityId, CancellationToken.None).ConfigureAwait(false);
                    if (meta != null)
                    {
                        await _cache.SetAsync(cacheKey, meta, TimeSpan.FromDays(30), CancellationToken.None).ConfigureAwait(false);
                        return meta;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExternalMetadataOrchestrator] GetAlbumMetadataAsync failed for '{providerName}:{providerEntityId}': {ex.Message}");
                }

                var stale = await _cache.GetWithMetadataAsync<ExternalAlbumMetadata>(cacheKey, allowStale: true).ConfigureAwait(false);
                return stale?.Value;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // SF-01: only THIS caller's personal wait was cancelled — the shared
            // fetch continues untouched for everyone else. Exit gracefully.
            return null;
        }
    }

    public async Task<ExternalArtistMetadata?> GetArtistMetadataAsync(
        string providerName,
        string providerEntityId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(providerEntityId))
            return null;

        string cacheKey = $"meta:artist:{providerName.ToLowerInvariant()}:{providerEntityId}";
        var cached = await _cache.GetAsync<ExternalArtistMetadata>(cacheKey, ct).ConfigureAwait(false);
        if (cached != null) return cached;

        string inFlightKey = $"get_artist:{cacheKey}";
        try
        {
            return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
            {
                var cachedAgain = await _cache.GetAsync<ExternalArtistMetadata>(cacheKey).ConfigureAwait(false);
                if (cachedAgain != null) return cachedAgain;

                var provider = _providers.FirstOrDefault(p => p.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) && p.IsEnabled);
                if (provider == null) return null;

                // SF-01: the shared fetch runs free of any single caller's token.
                try
                {
                    var meta = await provider.GetArtistMetadataAsync(providerEntityId, CancellationToken.None).ConfigureAwait(false);
                    if (meta != null)
                    {
                        await _cache.SetAsync(cacheKey, meta, TimeSpan.FromDays(30), CancellationToken.None).ConfigureAwait(false);
                        return meta;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExternalMetadataOrchestrator] GetArtistMetadataAsync failed for '{providerName}:{providerEntityId}': {ex.Message}");
                }

                var stale = await _cache.GetWithMetadataAsync<ExternalArtistMetadata>(cacheKey, allowStale: true).ConfigureAwait(false);
                return stale?.Value;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // SF-01: only THIS caller's personal wait was cancelled — the shared
            // fetch continues untouched for everyone else. Exit gracefully.
            return null;
        }
    }
}
