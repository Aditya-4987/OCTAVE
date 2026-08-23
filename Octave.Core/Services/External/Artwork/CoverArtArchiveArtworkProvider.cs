using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Network;

namespace Octave.Core.Services.External.Artwork;

public class CoverArtArchiveArtworkProvider : IExternalAlbumArtworkProvider
{
    private const string BaseUrl = "https://coverartarchive.org";
    private const string ProviderKey = "coverartarchive";

    private readonly IHttpService _httpService;
    private readonly IExternalMetadataProvider? _metadataProvider;
    private readonly Services.External.Settings.IExternalDataSettingsService? _settingsService;
    private readonly AsyncSingleFlight _singleFlight = new();
    private bool _isEnabled = true;

    public string ProviderName => "CoverArtArchive";
    public bool IsEnabled
    {
        get => _settingsService != null ? (_settingsService.CurrentSettings.CoverArtArchiveEnabled && _settingsService.CurrentSettings.EnableOnlineArtwork && !_settingsService.CurrentSettings.OfflineOnlyMode) : _isEnabled;
        set => _isEnabled = value;
    }
    public int Priority { get; set; } = 10;

    public CoverArtArchiveArtworkProvider(
        IHttpService httpService,
        IExternalMetadataProvider? metadataProvider = null,
        IProviderRateLimiterRegistry? rateLimiterRegistry = null,
        Services.External.Settings.IExternalDataSettingsService? settingsService = null)
    {
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
        _metadataProvider = metadataProvider;
        _settingsService = settingsService;

        // Respect CAA rate limits (1 req/sec)
        rateLimiterRegistry?.GetOrCreate(ProviderKey, TimeSpan.FromSeconds(1));
    }

    public async Task<IReadOnlyList<string>> SearchAlbumArtworkUrlsAsync(
        string albumTitle,
        string artistName,
        ExternalIds? externalIds = null,
        CancellationToken ct = default)
    {
        if (!IsEnabled || (string.IsNullOrWhiteSpace(albumTitle) && string.IsNullOrWhiteSpace(artistName) && externalIds == null))
        {
            return Array.Empty<string>();
        }

        string? releaseMbid = externalIds?.GetId("MusicBrainzReleaseId");
        string? releaseGroupMbid = externalIds?.GetId("MusicBrainzReleaseGroupId");

        // CAA-01: MusicBrainzId is entity-ambiguous — on track candidates it is
        // a RECORDING mbid, which just 404s against /release/{mbid}. Producers
        // stamp AdditionalIds[MusicBrainzEntityKind], and an explicit recording
        // or release-group stamp routes AWAY from the release endpoint (toward
        // the release-group endpoint or album search below). Unstamped ids
        // (legacy cache entries predating the contract) keep release semantics
        // so stored artwork lookups don't silently regress.
        string? entityKind = externalIds?.GetId(ExternalIdKinds.EntityKind);
        if (string.IsNullOrWhiteSpace(releaseMbid)
            && !string.IsNullOrWhiteSpace(externalIds?.MusicBrainzId)
            && !string.Equals(entityKind, ExternalIdKinds.KindRecording, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entityKind, ExternalIdKinds.KindReleaseGroup, StringComparison.OrdinalIgnoreCase))
        {
            releaseMbid = externalIds.MusicBrainzId;
        }

        // If no direct MBID is known, query metadata provider for release candidate MBIDs
        if (string.IsNullOrWhiteSpace(releaseMbid) && string.IsNullOrWhiteSpace(releaseGroupMbid) && _metadataProvider != null)
        {
            try
            {
                var albumCandidates = await _metadataProvider.SearchAlbumCandidatesAsync(albumTitle, artistName, null, ct).ConfigureAwait(false);
                var bestMatch = albumCandidates.FirstOrDefault(c => c.Confidence >= 0.70);
                if (bestMatch != null)
                {
                    releaseMbid = bestMatch.ExternalIds.MusicBrainzId;
                    releaseGroupMbid = bestMatch.ExternalIds.GetId("MusicBrainzReleaseGroupId");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CoverArtArchive] Metadata search fallback failed: {ex.Message}");
            }
        }

        var urls = new List<string>();

        // 1. Query Cover Art Archive Release endpoint if release MBID is known
        if (!string.IsNullOrWhiteSpace(releaseMbid))
        {
            string inFlightKey = $"caa_release:{releaseMbid}";
            var releaseUrls = await _singleFlight.ExecuteAsync(inFlightKey, async () =>
            {
                string requestUrl = $"{BaseUrl}/release/{Uri.EscapeDataString(releaseMbid)}";
                var httpResult = await _httpService.GetJsonAsync<CaaReleaseResponse>(
                    requestUrl,
                    ProviderKey,
                    null,
                    TimeSpan.FromSeconds(12),
                    ct).ConfigureAwait(false);

                if (httpResult.IsSuccess && httpResult.Data?.Images != null && httpResult.Data.Images.Count > 0)
                {
                    var found = new List<string>();
                    var frontImages = httpResult.Data.Images.Where(img => img.Front).ToList();
                    var candidateImages = frontImages.Count > 0 ? frontImages : httpResult.Data.Images;

                    foreach (var img in candidateImages)
                    {
                        if (!string.IsNullOrWhiteSpace(img.Thumbnails?.Thumb500))
                            found.Add(img.Thumbnails.Thumb500);
                        else if (!string.IsNullOrWhiteSpace(img.Thumbnails?.Large))
                            found.Add(img.Thumbnails.Large);
                        else if (!string.IsNullOrWhiteSpace(img.Thumbnails?.Thumb1200))
                            found.Add(img.Thumbnails.Thumb1200);
                        else if (!string.IsNullOrWhiteSpace(img.Image))
                            found.Add(img.Image);
                    }

                    return found;
                }

                // CAA-02: never fabricate a /front URL here. A JSON-fetch
                // failure is not evidence that art exists, and a guessed
                // /front 404s downstream indistinguishably from a real
                // result. 404 means art is definitively absent; anything
                // else is transient and simply yields no candidates.
                System.Diagnostics.Debug.WriteLine(
                    $"[CoverArtArchive] No images for release '{releaseMbid}' (HTTP {(int?)httpResult.StatusCode}).");
                return new List<string>();
            }).ConfigureAwait(false);

            if (releaseUrls != null && releaseUrls.Count > 0)
            {
                urls.AddRange(releaseUrls);
            }
        }

        // 2. Query Cover Art Archive Release-Group endpoint if release group MBID is known
        if (!string.IsNullOrWhiteSpace(releaseGroupMbid))
        {
            string inFlightKey = $"caa_rg:{releaseGroupMbid}";
            var rgUrls = await _singleFlight.ExecuteAsync(inFlightKey, async () =>
            {
                string requestUrl = $"{BaseUrl}/release-group/{Uri.EscapeDataString(releaseGroupMbid)}";
                var httpResult = await _httpService.GetJsonAsync<CaaReleaseResponse>(
                    requestUrl,
                    ProviderKey,
                    null,
                    TimeSpan.FromSeconds(12),
                    ct).ConfigureAwait(false);

                if (httpResult.IsSuccess && httpResult.Data?.Images != null && httpResult.Data.Images.Count > 0)
                {
                    var found = new List<string>();
                    var frontImages = httpResult.Data.Images.Where(img => img.Front).ToList();
                    var candidateImages = frontImages.Count > 0 ? frontImages : httpResult.Data.Images;

                    foreach (var img in candidateImages)
                    {
                        if (!string.IsNullOrWhiteSpace(img.Thumbnails?.Thumb500))
                            found.Add(img.Thumbnails.Thumb500);
                        else if (!string.IsNullOrWhiteSpace(img.Thumbnails?.Large))
                            found.Add(img.Thumbnails.Large);
                        else if (!string.IsNullOrWhiteSpace(img.Thumbnails?.Thumb1200))
                            found.Add(img.Thumbnails.Thumb1200);
                        else if (!string.IsNullOrWhiteSpace(img.Image))
                            found.Add(img.Image);
                    }

                    return found;
                }

                // CAA-02: same rule as the release endpoint — no speculative
                // /front guesses on failure.
                System.Diagnostics.Debug.WriteLine(
                    $"[CoverArtArchive] No images for release-group '{releaseGroupMbid}' (HTTP {(int?)httpResult.StatusCode}).");
                return new List<string>();
            }).ConfigureAwait(false);

            if (rgUrls != null && rgUrls.Count > 0)
            {
                urls.AddRange(rgUrls);
            }
        }

        return urls.Distinct().ToList();
    }
}
