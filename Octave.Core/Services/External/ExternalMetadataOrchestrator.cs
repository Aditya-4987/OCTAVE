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

        // 1. Check Cache
        var cached = await _cache.GetAsync<List<TrackMatchCandidate>>(cacheKey, ct).ConfigureAwait(false);
        if (cached != null && cached.Count > 0)
        {
            return cached;
        }

        // 2. Single-flight request across providers
        string inFlightKey = $"search_tracks:{cacheKey}";
        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var cachedAgain = await _cache.GetAsync<List<TrackMatchCandidate>>(cacheKey, ct).ConfigureAwait(false);
            if (cachedAgain != null && cachedAgain.Count > 0) return cachedAgain;

            var results = new List<TrackMatchCandidate>();
            var sortedProviders = _providers.Where(p => p.IsEnabled).OrderBy(p => p.Priority);

            foreach (var provider in sortedProviders)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var candidates = await provider.SearchTrackCandidatesAsync(title, artist, album, durationSeconds, ct).ConfigureAwait(false);
                    if (candidates != null && candidates.Count > 0)
                    {
                        results.AddRange(candidates);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExternalMetadataOrchestrator] Provider '{provider.ProviderName}' search failed: {ex.Message}");
                }
            }

            var sorted = results.OrderByDescending(c => c.Confidence).ToList();
            if (sorted.Count > 0)
            {
                // Cache search candidates with 7-day TTL
                await _cache.SetAsync(cacheKey, sorted, TimeSpan.FromDays(7), ct).ConfigureAwait(false);
                return (IReadOnlyList<TrackMatchCandidate>)sorted;
            }

            // Fallback: Check for stale cached results on provider failure/offline
            var staleEntry = await _cache.GetWithMetadataAsync<List<TrackMatchCandidate>>(cacheKey, allowStale: true, ct).ConfigureAwait(false);
            if (staleEntry != null && staleEntry.Value.Count > 0)
            {
                return staleEntry.Value;
            }

            return Array.Empty<TrackMatchCandidate>();
        }).ConfigureAwait(false) ?? Array.Empty<TrackMatchCandidate>();
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

        var cached = await _cache.GetAsync<List<AlbumMatchCandidate>>(cacheKey, ct).ConfigureAwait(false);
        if (cached != null && cached.Count > 0)
        {
            return cached;
        }

        string inFlightKey = $"search_albums:{cacheKey}";
        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var cachedAgain = await _cache.GetAsync<List<AlbumMatchCandidate>>(cacheKey, ct).ConfigureAwait(false);
            if (cachedAgain != null && cachedAgain.Count > 0) return cachedAgain;

            var results = new List<AlbumMatchCandidate>();
            var sortedProviders = _providers.Where(p => p.IsEnabled).OrderBy(p => p.Priority);

            foreach (var provider in sortedProviders)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var candidates = await provider.SearchAlbumCandidatesAsync(albumTitle, artistName, year, ct).ConfigureAwait(false);
                    if (candidates != null && candidates.Count > 0)
                    {
                        results.AddRange(candidates);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExternalMetadataOrchestrator] Provider '{provider.ProviderName}' album search failed: {ex.Message}");
                }
            }

            var sorted = results.OrderByDescending(c => c.Confidence).ToList();
            if (sorted.Count > 0)
            {
                await _cache.SetAsync(cacheKey, sorted, TimeSpan.FromDays(7), ct).ConfigureAwait(false);
                return (IReadOnlyList<AlbumMatchCandidate>)sorted;
            }

            var staleEntry = await _cache.GetWithMetadataAsync<List<AlbumMatchCandidate>>(cacheKey, allowStale: true, ct).ConfigureAwait(false);
            if (staleEntry != null && staleEntry.Value.Count > 0)
            {
                return staleEntry.Value;
            }

            return Array.Empty<AlbumMatchCandidate>();
        }).ConfigureAwait(false) ?? Array.Empty<AlbumMatchCandidate>();
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

        var cached = await _cache.GetAsync<List<ArtistMatchCandidate>>(cacheKey, ct).ConfigureAwait(false);
        if (cached != null && cached.Count > 0)
        {
            return cached;
        }

        string inFlightKey = $"search_artists:{cacheKey}";
        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var cachedAgain = await _cache.GetAsync<List<ArtistMatchCandidate>>(cacheKey, ct).ConfigureAwait(false);
            if (cachedAgain != null && cachedAgain.Count > 0) return cachedAgain;

            var results = new List<ArtistMatchCandidate>();
            var sortedProviders = _providers.Where(p => p.IsEnabled).OrderBy(p => p.Priority);

            foreach (var provider in sortedProviders)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var candidates = await provider.SearchArtistCandidatesAsync(artistName, ct).ConfigureAwait(false);
                    if (candidates != null && candidates.Count > 0)
                    {
                        results.AddRange(candidates);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExternalMetadataOrchestrator] Provider '{provider.ProviderName}' artist search failed: {ex.Message}");
                }
            }

            var sorted = results.OrderByDescending(c => c.Confidence).ToList();
            if (sorted.Count > 0)
            {
                await _cache.SetAsync(cacheKey, sorted, TimeSpan.FromDays(7), ct).ConfigureAwait(false);
                return (IReadOnlyList<ArtistMatchCandidate>)sorted;
            }

            var staleEntry = await _cache.GetWithMetadataAsync<List<ArtistMatchCandidate>>(cacheKey, allowStale: true, ct).ConfigureAwait(false);
            if (staleEntry != null && staleEntry.Value.Count > 0)
            {
                return staleEntry.Value;
            }

            return Array.Empty<ArtistMatchCandidate>();
        }).ConfigureAwait(false) ?? Array.Empty<ArtistMatchCandidate>();
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
        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var cachedAgain = await _cache.GetAsync<ExternalTrackMetadata>(cacheKey, ct).ConfigureAwait(false);
            if (cachedAgain != null) return cachedAgain;

            var provider = _providers.FirstOrDefault(p => p.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) && p.IsEnabled);
            if (provider == null) return null;

            try
            {
                var meta = await provider.GetTrackMetadataAsync(providerEntityId, ct).ConfigureAwait(false);
                if (meta != null)
                {
                    await _cache.SetAsync(cacheKey, meta, TimeSpan.FromDays(30), ct).ConfigureAwait(false);
                    return meta;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExternalMetadataOrchestrator] GetTrackMetadataAsync failed for '{providerName}:{providerEntityId}': {ex.Message}");
            }

            // Stale fallback on network error/offline
            var stale = await _cache.GetWithMetadataAsync<ExternalTrackMetadata>(cacheKey, allowStale: true, ct).ConfigureAwait(false);
            return stale?.Value;
        }).ConfigureAwait(false);
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
        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var cachedAgain = await _cache.GetAsync<ExternalAlbumMetadata>(cacheKey, ct).ConfigureAwait(false);
            if (cachedAgain != null) return cachedAgain;

            var provider = _providers.FirstOrDefault(p => p.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) && p.IsEnabled);
            if (provider == null) return null;

            try
            {
                var meta = await provider.GetAlbumMetadataAsync(providerEntityId, ct).ConfigureAwait(false);
                if (meta != null)
                {
                    await _cache.SetAsync(cacheKey, meta, TimeSpan.FromDays(30), ct).ConfigureAwait(false);
                    return meta;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExternalMetadataOrchestrator] GetAlbumMetadataAsync failed for '{providerName}:{providerEntityId}': {ex.Message}");
            }

            var stale = await _cache.GetWithMetadataAsync<ExternalAlbumMetadata>(cacheKey, allowStale: true, ct).ConfigureAwait(false);
            return stale?.Value;
        }).ConfigureAwait(false);
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
        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var cachedAgain = await _cache.GetAsync<ExternalArtistMetadata>(cacheKey, ct).ConfigureAwait(false);
            if (cachedAgain != null) return cachedAgain;

            var provider = _providers.FirstOrDefault(p => p.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) && p.IsEnabled);
            if (provider == null) return null;

            try
            {
                var meta = await provider.GetArtistMetadataAsync(providerEntityId, ct).ConfigureAwait(false);
                if (meta != null)
                {
                    await _cache.SetAsync(cacheKey, meta, TimeSpan.FromDays(30), ct).ConfigureAwait(false);
                    return meta;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExternalMetadataOrchestrator] GetArtistMetadataAsync failed for '{providerName}:{providerEntityId}': {ex.Message}");
            }

            var stale = await _cache.GetWithMetadataAsync<ExternalArtistMetadata>(cacheKey, allowStale: true, ct).ConfigureAwait(false);
            return stale?.Value;
        }).ConfigureAwait(false);
    }
}
