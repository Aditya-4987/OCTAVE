using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Metadata;
using Octave.Core.Services.Network;

namespace Octave.Core.Services.External.Lyrics;

public class LrcLibLyricsProvider : IExternalLyricsProvider
{
    private const string BaseUrl = "https://lrclib.net/api";
    private const string ProviderKey = "lrclib";

    private readonly IHttpService _httpService;
    private readonly Services.External.Settings.IExternalDataSettingsService? _settingsService;
    private readonly AsyncSingleFlight _singleFlight = new();
    private bool _isEnabled = true;

    public string ProviderName => "LrcLib";
    public bool IsEnabled
    {
        get => _settingsService != null ? (_settingsService.CurrentSettings.LrcLibEnabled && _settingsService.CurrentSettings.EnableOnlineLyrics && !_settingsService.CurrentSettings.OfflineOnlyMode) : _isEnabled;
        set => _isEnabled = value;
    }
    public int Priority { get; set; } = 10;

    public LrcLibLyricsProvider(
        IHttpService httpService,
        IProviderRateLimiterRegistry? rateLimiterRegistry = null,
        Services.External.Settings.IExternalDataSettingsService? settingsService = null)
    {
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
        _settingsService = settingsService;
        rateLimiterRegistry?.GetOrCreate(ProviderKey, TimeSpan.FromMilliseconds(500));
    }

    public async Task<ExternalLyricsResult?> FetchLyricsAsync(
        string title,
        string artist,
        string? album = null,
        double? durationSeconds = null,
        ExternalIds? externalIds = null,
        CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        // LRC-01: the dedup key must distinguish releases — same title+artist
        // on different albums (or different-duration versions) must not share
        // one in-flight result and receive each other's lyrics. Normalized
        // album plus rounded seconds; 0 keeps "unknown" lookups sharing.
        string albumPart = !string.IsNullOrWhiteSpace(album) ? MetadataTextNormalizer.Normalize(album) : "-";
        int durationPart = durationSeconds.HasValue && durationSeconds.Value > 0
            ? (int)Math.Round(durationSeconds.Value)
            : 0;
        string inFlightKey = $"lrclib:{artist.Trim().ToLowerInvariant()}:{title.Trim().ToLowerInvariant()}:{albumPart}:{durationPart}";

        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            // 1. Exact lookup with duration and album if available
            string requestUrl = BuildGetUrl(title, artist, album, durationSeconds);

            var httpResult = await _httpService.GetJsonAsync<LrcLibGetDto>(
                requestUrl,
                ProviderKey,
                null,
                TimeSpan.FromSeconds(10),
                ct).ConfigureAwait(false);

            // 2. If exact lookup failed and album/duration was specified, retry without album and duration
            if ((!httpResult.IsSuccess || httpResult.Data == null) && (!string.IsNullOrWhiteSpace(album) || durationSeconds.HasValue))
            {
                string fallbackUrl = BuildGetUrl(title, artist, null, null);
                httpResult = await _httpService.GetJsonAsync<LrcLibGetDto>(
                    fallbackUrl,
                    ProviderKey,
                    null,
                    TimeSpan.FromSeconds(10),
                    ct).ConfigureAwait(false);
            }

            // LRC-02: both constrained /get attempts missed — the old third
            // attempt repeated the same exact query. A fuzzy /api/search with
            // a scored, duration-aware pick still finds slightly-off
            // title/artist variants.
            if ((!httpResult.IsSuccess || httpResult.Data == null))
            {
                var fuzzy = await FetchBestSearchMatchAsync(title, artist, durationSeconds, ct).ConfigureAwait(false);
                if (fuzzy != null)
                {
                    httpResult = HttpResult<LrcLibGetDto>.Success(fuzzy);
                }
            }

            if (!httpResult.IsSuccess || httpResult.Data == null)
            {
                return null;
            }

            var dto = httpResult.Data;

            // 3. Parse Synchronized Lyrics if available
            if (!string.IsNullOrWhiteSpace(dto.SyncedLyrics))
            {
                var parsed = LyricsService.ParseLrcContent(dto.Id?.ToString() ?? "", dto.SyncedLyrics);
                if (parsed.State == LyricsState.Synced && parsed.SyncedLines != null && parsed.SyncedLines.Count > 0)
                {
                    return new ExternalLyricsResult(
                        dto.Id?.ToString(),
                        LyricsState.Synced,
                        parsed.SyncedLines,
                        dto.PlainLyrics,
                        ProviderName);
                }
            }

            // 4. Plain Lyrics fallback
            if (!string.IsNullOrWhiteSpace(dto.PlainLyrics))
            {
                return new ExternalLyricsResult(
                    dto.Id?.ToString(),
                    LyricsState.Unsynced,
                    null,
                    dto.PlainLyrics.Trim(),
                    ProviderName);
            }

            // 5. Instrumental track
            if (dto.Instrumental == true)
            {
                return new ExternalLyricsResult(
                    dto.Id?.ToString(),
                    LyricsState.Unsynced,
                    null,
                    "♪ Instrumental ♪",
                    ProviderName);
            }

            return null;
        }).ConfigureAwait(false);
    }

    private static string BuildGetUrl(string title, string artist, string? album, double? durationSeconds)
    {
        var queryParams = new List<string>
        {
            $"track_name={Uri.EscapeDataString(title.Trim())}",
            $"artist_name={Uri.EscapeDataString(artist.Trim())}"
        };

        if (!string.IsNullOrWhiteSpace(album))
        {
            queryParams.Add($"album_name={Uri.EscapeDataString(album.Trim())}");
        }

        if (durationSeconds.HasValue && durationSeconds.Value > 0)
        {
            queryParams.Add($"duration={(int)Math.Round(durationSeconds.Value)}");
        }

        return $"{BaseUrl}/get?{string.Join("&", queryParams)}";
    }

    // LRC-02: /api/search is lrclib's fuzzy endpoint — query it and keep the
    // best-scoring result instead of repeating the exact /get that already
    // missed. Duration agreement breaks ties; a floor keeps garbage results
    // out when nothing really matches.
    private async Task<LrcLibGetDto?> FetchBestSearchMatchAsync(string title, string artist, double? durationSeconds, CancellationToken ct)
    {
        string searchUrl = $"{BaseUrl}/search?track_name={Uri.EscapeDataString(title.Trim())}&artist_name={Uri.EscapeDataString(artist.Trim())}";

        var httpResult = await _httpService.GetJsonAsync<List<LrcLibGetDto>>(
            searchUrl,
            ProviderKey,
            null,
            TimeSpan.FromSeconds(10),
            ct).ConfigureAwait(false);

        if (!httpResult.IsSuccess || httpResult.Data == null || httpResult.Data.Count == 0)
        {
            return null;
        }

        LrcLibGetDto? best = null;
        double bestScore = 0;

        foreach (var r in httpResult.Data)
        {
            double score = 0.5 * MetadataTextNormalizer.CalculateSimilarity(title, r.TrackName) +
                           0.5 * MetadataTextNormalizer.CalculateSimilarity(artist, r.ArtistName);

            if (durationSeconds.HasValue && durationSeconds.Value > 0 && r.Duration is > 0)
            {
                double diff = Math.Abs(r.Duration.Value - durationSeconds.Value);
                score += diff <= 2 ? 0.15 : diff <= 5 ? 0.05 : -0.10;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = r;
            }
        }

        return bestScore >= 0.60 ? best : null;
    }
}
