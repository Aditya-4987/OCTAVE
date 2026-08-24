using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Interfaces;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Cache;
using Octave.Core.Services.Network;

namespace Octave.Core.Services.External.Artist;

public class ArtistEnrichmentService : IArtistEnrichmentService
{
    private readonly IEnumerable<IArtistEnrichmentProvider> _providers;
    private readonly IHttpService _httpService;
    private readonly IArtworkCacheManager _artworkCacheManager;
    private readonly IExternalDataCache _cache;
    private readonly AsyncSingleFlight _singleFlight = new();

    public ArtistEnrichmentService(
        IEnumerable<IArtistEnrichmentProvider> providers,
        IHttpService httpService,
        IArtworkCacheManager artworkCacheManager,
        IExternalDataCache cache)
    {
        _providers = providers ?? Array.Empty<IArtistEnrichmentProvider>();
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
        _artworkCacheManager = artworkCacheManager ?? throw new ArgumentNullException(nameof(artworkCacheManager));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<EnrichedArtistProfile?> GetEnrichedArtistAsync(
        string artistName,
        string? musicBrainzArtistId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName) && string.IsNullOrWhiteSpace(musicBrainzArtistId))
            return null;

        string cacheKey = !string.IsNullOrWhiteSpace(musicBrainzArtistId)
            ? $"artist_enrich:mbid:{musicBrainzArtistId.Trim()}"
            : $"artist_enrich:name:{MetadataTextNormalizer.Normalize(artistName)}";

        // 1. Check Two-Tier Cache
        var cached = await _cache.GetAsync<EnrichedArtistProfile>(cacheKey, ct).ConfigureAwait(false);
        if (cached != null)
        {
            if (!string.IsNullOrWhiteSpace(cached.LocalImageToken))
            {
                string rel = cached.LocalImageToken.Replace("ArtworkCache/", "");
                string abs = Path.Combine(_artworkCacheManager.CacheRoot, rel);
                if (File.Exists(abs))
                {
                    return cached;
                }
            }
            else
            {
                return cached;
            }
        }

        // 2. Deduplicate concurrent requests
        string inFlightKey = $"enrich_artist:{cacheKey}";
        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var cachedAgain = await _cache.GetAsync<EnrichedArtistProfile>(cacheKey, ct).ConfigureAwait(false);
            if (cachedAgain != null) return cachedAgain;

            var sortedProviders = _providers.Where(p => p.IsEnabled).OrderBy(p => p.Priority).ToList();

            foreach (var provider in sortedProviders)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    EnrichedArtistProfile? profile = null;

                    // Priority 1: Use stable MusicBrainz Artist ID when available
                    if (!string.IsNullOrWhiteSpace(musicBrainzArtistId))
                    {
                        profile = await provider.GetArtistProfileByMbidAsync(musicBrainzArtistId, ct).ConfigureAwait(false);
                    }

                    // Priority 2: Fallback to artist name query only when ID is missing or returned no result
                    if (profile == null && !string.IsNullOrWhiteSpace(artistName))
                    {
                        profile = await provider.GetArtistProfileByNameAsync(artistName, ct).ConfigureAwait(false);
                    }

                    if (profile != null)
                    {
                        string? localImageToken = null;

                        // Download & Cache Artist Photo if available
                        if (profile.ExternalLinks != null &&
                            profile.ExternalLinks.TryGetValue("ImageThumb", out var imageUrl) &&
                            !string.IsNullOrWhiteSpace(imageUrl))
                        {
                            var imgHttp = await _httpService.GetByteArrayAsync(
                                imageUrl,
                                provider.ProviderName,
                                null,
                                TimeSpan.FromSeconds(10),
                                ct).ConfigureAwait(false);

                            if (imgHttp.IsSuccess && imgHttp.Data != null)
                            {
                                if (ImageValidator.IsValidImage(imgHttp.Data, out string detectedMime))
                                {
                                    localImageToken = await _artworkCacheManager.CacheBytesAsync(imgHttp.Data, detectedMime).ConfigureAwait(false);
                                }
                            }
                        }

                        var enrichedWithLocalImage = profile with { LocalImageToken = localImageToken };

                        // PROV-04: a 30-day TTL was applied even when no image token
                        // resolved (provider had no photo / download failed), pinning a
                        // bio-only profile for a month. Profiles WITH a local image
                        // keep the long TTL; imageless ones expire quickly so the next
                        // enrichment attempt can pick the photo up.
                        TimeSpan cacheTtl = !string.IsNullOrWhiteSpace(localImageToken)
                            ? TimeSpan.FromDays(30)
                            : TimeSpan.FromHours(6);
                        await _cache.SetAsync(cacheKey, enrichedWithLocalImage, cacheTtl, ct).ConfigureAwait(false);
                        return enrichedWithLocalImage;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ArtistEnrichmentService] Provider '{provider.ProviderName}' failed: {ex.Message}");
                }
            }

            // Fallback: Check stale cache on provider failure / offline
            var stale = await _cache.GetWithMetadataAsync<EnrichedArtistProfile>(cacheKey, allowStale: true, ct).ConfigureAwait(false);
            return stale?.Value;
        }).ConfigureAwait(false);
    }
}
