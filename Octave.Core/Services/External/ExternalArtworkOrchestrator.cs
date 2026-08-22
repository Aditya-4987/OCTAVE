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

namespace Octave.Core.Services.External;

public class ExternalArtworkOrchestrator : IExternalArtworkOrchestrator
{
    private readonly IEnumerable<IExternalAlbumArtworkProvider> _albumArtworkProviders;
    private readonly IEnumerable<IExternalArtistImageProvider> _artistImageProviders;
    private readonly IHttpService _httpService;
    private readonly IArtworkCacheManager _artworkCacheManager;
    private readonly IExternalDataCache _cache;
    private readonly AsyncSingleFlight _singleFlight = new();

    public ExternalArtworkOrchestrator(
        IEnumerable<IExternalAlbumArtworkProvider> albumArtworkProviders,
        IEnumerable<IExternalArtistImageProvider> artistImageProviders,
        IHttpService httpService,
        IArtworkCacheManager artworkCacheManager,
        IExternalDataCache cache)
    {
        _albumArtworkProviders = albumArtworkProviders ?? Array.Empty<IExternalAlbumArtworkProvider>();
        _artistImageProviders = artistImageProviders ?? Array.Empty<IExternalArtistImageProvider>();
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
        _artworkCacheManager = artworkCacheManager ?? throw new ArgumentNullException(nameof(artworkCacheManager));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<string?> ResolveAndCacheAlbumArtworkAsync(
        string albumTitle,
        string artistName,
        ExternalIds? externalIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(albumTitle) && string.IsNullOrWhiteSpace(artistName))
            return null;

        string normArtist = MetadataTextNormalizer.Normalize(artistName);
        string normAlbum = MetadataTextNormalizer.Normalize(albumTitle);
        string cacheKey = $"art:album:{normArtist}:{normAlbum}";

        // 1. Check cache for previously resolved local token
        var cachedToken = await _cache.GetAsync<string>(cacheKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cachedToken))
        {
            // Verify file actually exists in artwork cache directory
            string relativePath = cachedToken.Replace("ArtworkCache/", "");
            string absolutePath = Path.Combine(_artworkCacheManager.CacheRoot, relativePath);
            if (File.Exists(absolutePath))
            {
                return cachedToken;
            }
        }

        // 2. Deduplicate concurrent in-flight requests for the same album
        string inFlightKey = $"resolve_album_art:{cacheKey}";
        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            // Double check cache inside single-flight
            var tokenAgain = await _cache.GetAsync<string>(cacheKey, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(tokenAgain))
            {
                string relativePath = tokenAgain.Replace("ArtworkCache/", "");
                string absolutePath = Path.Combine(_artworkCacheManager.CacheRoot, relativePath);
                if (File.Exists(absolutePath))
                {
                    return tokenAgain;
                }
            }

            var sortedProviders = _albumArtworkProviders.Where(p => p.IsEnabled).OrderBy(p => p.Priority);

            foreach (var provider in sortedProviders)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var candidateUrls = await provider.SearchAlbumArtworkUrlsAsync(albumTitle, artistName, externalIds, ct).ConfigureAwait(false);
                    if (candidateUrls != null && candidateUrls.Count > 0)
                    {
                        foreach (var url in candidateUrls)
                        {
                            if (string.IsNullOrWhiteSpace(url) || ct.IsCancellationRequested) continue;

                            var byteResult = await _httpService.GetByteArrayAsync(
                                url,
                                provider.ProviderName,
                                null,
                                TimeSpan.FromSeconds(15),
                                ct).ConfigureAwait(false);

                            if (byteResult.IsSuccess && byteResult.Data != null)
                            {
                                // Validate image bytes before caching to reject corrupted data/HTML error pages
                                if (ImageValidator.IsValidImage(byteResult.Data, out string detectedMimeType))
                                {
                                    string? token = await _artworkCacheManager.CacheBytesAsync(byteResult.Data, detectedMimeType).ConfigureAwait(false);
                                    if (!string.IsNullOrWhiteSpace(token))
                                    {
                                        await _cache.SetAsync(cacheKey, token, TimeSpan.FromDays(90), ct).ConfigureAwait(false);
                                        return token;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExternalArtworkOrchestrator] Album artwork provider '{provider.ProviderName}' failed: {ex.Message}");
                }
            }

            // Fallback: Check stale cache on provider failure / offline
            var stale = await _cache.GetWithMetadataAsync<string>(cacheKey, allowStale: true, ct).ConfigureAwait(false);
            if (stale != null && !string.IsNullOrWhiteSpace(stale.Value))
            {
                string relativePath = stale.Value.Replace("ArtworkCache/", "");
                string absolutePath = Path.Combine(_artworkCacheManager.CacheRoot, relativePath);
                if (File.Exists(absolutePath))
                {
                    return stale.Value;
                }
            }

            return null;
        }).ConfigureAwait(false);
    }

    public async Task<string?> ResolveAndCacheArtistImageAsync(
        string artistName,
        ExternalIds? externalIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return null;

        string normArtist = MetadataTextNormalizer.Normalize(artistName);
        string cacheKey = $"art:artist:{normArtist}";

        // 1. Check cache
        var cachedToken = await _cache.GetAsync<string>(cacheKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cachedToken))
        {
            string relativePath = cachedToken.Replace("ArtworkCache/", "");
            string absolutePath = Path.Combine(_artworkCacheManager.CacheRoot, relativePath);
            if (File.Exists(absolutePath))
            {
                return cachedToken;
            }
        }

        // 2. Deduplicate concurrent in-flight requests for the same artist
        string inFlightKey = $"resolve_artist_art:{cacheKey}";
        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var tokenAgain = await _cache.GetAsync<string>(cacheKey, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(tokenAgain))
            {
                string relativePath = tokenAgain.Replace("ArtworkCache/", "");
                string absolutePath = Path.Combine(_artworkCacheManager.CacheRoot, relativePath);
                if (File.Exists(absolutePath))
                {
                    return tokenAgain;
                }
            }

            var sortedProviders = _artistImageProviders.Where(p => p.IsEnabled).OrderBy(p => p.Priority);

            foreach (var provider in sortedProviders)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var candidateUrls = await provider.SearchArtistImageUrlsAsync(artistName, externalIds, ct).ConfigureAwait(false);
                    if (candidateUrls != null && candidateUrls.Count > 0)
                    {
                        foreach (var url in candidateUrls)
                        {
                            if (string.IsNullOrWhiteSpace(url) || ct.IsCancellationRequested) continue;

                            var byteResult = await _httpService.GetByteArrayAsync(
                                url,
                                provider.ProviderName,
                                null,
                                TimeSpan.FromSeconds(15),
                                ct).ConfigureAwait(false);

                            if (byteResult.IsSuccess && byteResult.Data != null)
                            {
                                if (ImageValidator.IsValidImage(byteResult.Data, out string detectedMimeType))
                                {
                                    string? token = await _artworkCacheManager.CacheBytesAsync(byteResult.Data, detectedMimeType).ConfigureAwait(false);
                                    if (!string.IsNullOrWhiteSpace(token))
                                    {
                                        await _cache.SetAsync(cacheKey, token, TimeSpan.FromDays(90), ct).ConfigureAwait(false);
                                        return token;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExternalArtworkOrchestrator] Artist image provider '{provider.ProviderName}' failed: {ex.Message}");
                }
            }

            // Fallback: Check stale cache on provider failure / offline
            var stale = await _cache.GetWithMetadataAsync<string>(cacheKey, allowStale: true, ct).ConfigureAwait(false);
            if (stale != null && !string.IsNullOrWhiteSpace(stale.Value))
            {
                string relativePath = stale.Value.Replace("ArtworkCache/", "");
                string absolutePath = Path.Combine(_artworkCacheManager.CacheRoot, relativePath);
                if (File.Exists(absolutePath))
                {
                    return stale.Value;
                }
            }

            return null;
        }).ConfigureAwait(false);
    }
}
