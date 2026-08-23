using System;
using System.Collections.Generic;
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

        // 1. Check cache for previously resolved local token. ORC-03: existence
        // probing is delegated to the manager (token layout + memoized probe).
        var cachedToken = await _cache.GetAsync<string>(cacheKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cachedToken) && _artworkCacheManager.CachedFileExists(cachedToken))
        {
            return cachedToken;
        }

        // 2. Deduplicate concurrent in-flight requests for the same album
        string inFlightKey = $"resolve_album_art:{cacheKey}";
        try
        {
            return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
            {
                // Double check cache inside single-flight
                var tokenAgain = await _cache.GetAsync<string>(cacheKey).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(tokenAgain) && _artworkCacheManager.CachedFileExists(tokenAgain))
                {
                    return tokenAgain;
                }

                var sortedProviders = _albumArtworkProviders.Where(p => p.IsEnabled).OrderBy(p => p.Priority);

                // SF-01: the shared factory never observes a caller's token — the
                // sweep runs to completion under its own 15s-per-request timeouts so
                // one caller cancelling cannot corrupt the result other joiners are
                // awaiting. The caller's own ct is applied by AsyncSingleFlight to
                // their personal wait only.
                foreach (var provider in sortedProviders)
                {
                    try
                    {
                        var candidateUrls = await provider.SearchAlbumArtworkUrlsAsync(albumTitle, artistName, externalIds, CancellationToken.None).ConfigureAwait(false);
                        if (candidateUrls != null && candidateUrls.Count > 0)
                        {
                            foreach (var url in candidateUrls)
                            {
                                if (string.IsNullOrWhiteSpace(url)) continue;

                                var byteResult = await _httpService.GetByteArrayAsync(
                                    url,
                                    provider.ProviderName,
                                    null,
                                    TimeSpan.FromSeconds(15),
                                    CancellationToken.None).ConfigureAwait(false);

                                if (byteResult.IsSuccess && byteResult.Data != null)
                                {
                                    // Validate image bytes before caching to reject corrupted data/HTML error pages
                                    if (ImageValidator.IsValidImage(byteResult.Data, out string detectedMimeType))
                                    {
                                        string? token = await _artworkCacheManager.CacheBytesAsync(byteResult.Data, detectedMimeType).ConfigureAwait(false);
                                        if (!string.IsNullOrWhiteSpace(token))
                                        {
                                            await _cache.SetAsync(cacheKey, token, TimeSpan.FromDays(90), CancellationToken.None).ConfigureAwait(false);
                                            return token;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ExternalArtworkOrchestrator] Album artwork provider '{provider.ProviderName}' failed: {ex.Message}");
                    }
                }

                // Fallback: Check stale cache on provider failure / offline
                var stale = await _cache.GetWithMetadataAsync<string>(cacheKey, allowStale: true).ConfigureAwait(false);
                if (stale != null && !string.IsNullOrWhiteSpace(stale.Value) && _artworkCacheManager.CachedFileExists(stale.Value))
                {
                    return stale.Value;
                }

                return null;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // SF-01: this caller's personal wait was cancelled; the shared fetch
            // continues for everyone else. Exit gracefully per the established
            // orchestrator contract.
            return null;
        }
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
        if (!string.IsNullOrWhiteSpace(cachedToken) && _artworkCacheManager.CachedFileExists(cachedToken))
        {
            return cachedToken;
        }

        // 2. Deduplicate concurrent in-flight requests for the same artist
        string inFlightKey = $"resolve_artist_art:{cacheKey}";
        try
        {
            return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
            {
                var tokenAgain = await _cache.GetAsync<string>(cacheKey).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(tokenAgain) && _artworkCacheManager.CachedFileExists(tokenAgain))
                {
                    return tokenAgain;
                }

                var sortedProviders = _artistImageProviders.Where(p => p.IsEnabled).OrderBy(p => p.Priority);

                // SF-01: shared sweep runs free of any single caller's token.
                foreach (var provider in sortedProviders)
                {
                    try
                    {
                        var candidateUrls = await provider.SearchArtistImageUrlsAsync(artistName, externalIds, CancellationToken.None).ConfigureAwait(false);
                        if (candidateUrls != null && candidateUrls.Count > 0)
                        {
                            foreach (var url in candidateUrls)
                            {
                                if (string.IsNullOrWhiteSpace(url)) continue;

                                var byteResult = await _httpService.GetByteArrayAsync(
                                    url,
                                    provider.ProviderName,
                                    null,
                                    TimeSpan.FromSeconds(15),
                                    CancellationToken.None).ConfigureAwait(false);

                                if (byteResult.IsSuccess && byteResult.Data != null)
                                {
                                    if (ImageValidator.IsValidImage(byteResult.Data, out string detectedMimeType))
                                    {
                                        string? token = await _artworkCacheManager.CacheBytesAsync(byteResult.Data, detectedMimeType).ConfigureAwait(false);
                                        if (!string.IsNullOrWhiteSpace(token))
                                        {
                                            await _cache.SetAsync(cacheKey, token, TimeSpan.FromDays(90), CancellationToken.None).ConfigureAwait(false);
                                            return token;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ExternalArtworkOrchestrator] Artist image provider '{provider.ProviderName}' failed: {ex.Message}");
                    }
                }

                // Fallback: Check stale cache on provider failure / offline
                var stale = await _cache.GetWithMetadataAsync<string>(cacheKey, allowStale: true).ConfigureAwait(false);
                if (stale != null && !string.IsNullOrWhiteSpace(stale.Value) && _artworkCacheManager.CachedFileExists(stale.Value))
                {
                    return stale.Value;
                }

                return null;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // SF-01: this caller's personal wait was cancelled; the shared fetch
            // continues for everyone else. Exit gracefully.
            return null;
        }
    }
}
