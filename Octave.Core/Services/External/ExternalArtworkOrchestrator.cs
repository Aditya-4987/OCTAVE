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
        CancellationToken ct = default,
        bool preferHighResolutionUpgrade = false)
    {
        if (string.IsNullOrWhiteSpace(albumTitle) && string.IsNullOrWhiteSpace(artistName))
            return null;

        string normArtist = MetadataTextNormalizer.Normalize(artistName);
        string normAlbum = MetadataTextNormalizer.Normalize(albumTitle);
        string cacheKey = $"art:album:{normArtist}:{normAlbum}";

        // 1. Check cache for previously resolved local token. ORC-03: existence
        // probing is delegated to the manager (token layout + memoized probe).
        var cachedToken = await _cache.GetAsync<string>(cacheKey, ct).ConfigureAwait(false);
        bool upgradeRequested = false;
        if (!string.IsNullOrWhiteSpace(cachedToken) && _artworkCacheManager.CachedFileExists(cachedToken))
        {
            // NF-31: a cached file below the hi-res bar is served as-is by
            // playback callers, but an upgrade-requesting caller re-sweeps the
            // providers once per cooldown so pre-fix 500px entries self-heal on
            // the next scan. The old token stays cached as the stale fallback.
            if (!preferHighResolutionUpgrade || !await IsLowResolutionAndDueAsync(cachedToken, cacheKey, ct).ConfigureAwait(false))
            {
                return cachedToken;
            }
            upgradeRequested = true;
        }

        // 2. Deduplicate concurrent in-flight requests for the same album
        string inFlightKey = $"resolve_album_art:{cacheKey}";
        try
        {
            return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
            {
                // Double check cache inside single-flight. NF-31: an upgrade
                // pass deliberately skips this - the whole point is to replace
                // the (valid but low-res) cached file.
                var tokenAgain = await _cache.GetAsync<string>(cacheKey).ConfigureAwait(false);
                if (!upgradeRequested && !string.IsNullOrWhiteSpace(tokenAgain) && _artworkCacheManager.CachedFileExists(tokenAgain))
                {
                    return tokenAgain;
                }

                if (upgradeRequested)
                {
                    // Memoize BEFORE sweeping so a provider sweep with no better
                    // art isn't repeated on every scan until the cooldown lapses.
                    await _cache.SetAsync(UpgradeMemoKey(cacheKey), DateTimeOffset.UtcNow, UpgradeMemoTtl, CancellationToken.None).ConfigureAwait(false);
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

    // NF-31: below this encoded width a cached cover is considered low-res for
    // the NowPlaying view and the ambient backdrop (~1200+ physical px on the
    // displays where those are actually looked at).
    internal const int MinHighResWidth = 1000;
    private static readonly TimeSpan UpgradeCooldown = TimeSpan.FromDays(7);
    private static readonly TimeSpan UpgradeMemoTtl = TimeSpan.FromDays(30);

    private static string UpgradeMemoKey(string cacheKey) => $"{cacheKey}:hires-attempt";

    // Width probe + cooldown read. The memo itself is written by the sweep so a
    // provider pass with nothing better isn't retried until the cooldown lapses.
    private async Task<bool> IsLowResolutionAndDueAsync(string cachedToken, string cacheKey, CancellationToken ct)
    {
        var lastAttempt = await _cache.GetAsync<DateTimeOffset>(UpgradeMemoKey(cacheKey), ct).ConfigureAwait(false);
        if (lastAttempt != default && DateTimeOffset.UtcNow - lastAttempt < UpgradeCooldown)
        {
            return false;
        }

        int? width = ImageDimensionReader.TryReadWidth(_artworkCacheManager.ResolveTokenPath(cachedToken));
        return width != null && width.Value < MinHighResWidth;
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
