using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Network;

namespace Octave.Core.Services.External.Artist;

public class TheAudioDbArtistEnrichmentProvider : IArtistEnrichmentProvider, IExternalArtistImageProvider
{
    private const string ProviderKey = "theaudiodb";
    private readonly IHttpService _httpService;
    private readonly TheAudioDbOptions _options;
    private readonly Settings.IExternalDataSettingsService? _settingsService;
    private readonly AsyncSingleFlight _singleFlight = new();
    private bool _isEnabled = true;

    public string ProviderName => "TheAudioDB";
    public bool IsEnabled
    {
        get => _settingsService != null ? (_settingsService.CurrentSettings.TheAudioDbEnabled && _settingsService.CurrentSettings.EnableArtistEnrichment && !_settingsService.CurrentSettings.OfflineOnlyMode && !string.IsNullOrWhiteSpace(_settingsService.CurrentSettings.TheAudioDbApiKey)) : _isEnabled;
        set => _isEnabled = value;
    }
    public int Priority { get; set; } = 10;

    public TheAudioDbArtistEnrichmentProvider(
        IHttpService httpService,
        TheAudioDbOptions? options = null,
        IProviderRateLimiterRegistry? rateLimiterRegistry = null,
        Settings.IExternalDataSettingsService? settingsService = null)
    {
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
        _options = options ?? new TheAudioDbOptions();
        _settingsService = settingsService;

        // TheAudioDB public rate limit: 1 request per 500ms
        rateLimiterRegistry?.GetOrCreate(ProviderKey, TimeSpan.FromMilliseconds(500));
    }

    public async Task<EnrichedArtistProfile?> GetArtistProfileByMbidAsync(
        string musicBrainzArtistId,
        CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(musicBrainzArtistId))
            return null;

        string inFlightKey = $"tadb_mbid:{musicBrainzArtistId}";

        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            string apiKey = _settingsService?.CurrentSettings.TheAudioDbApiKey?.Trim() ?? _options.ApiKey;
            string url = $"{_options.BaseUrl}/{apiKey}/artist-mb.php?i={Uri.EscapeDataString(musicBrainzArtistId)}";

            var httpResult = await _httpService.GetJsonAsync<TadbArtistResponse>(
                url,
                ProviderKey,
                null,
                TimeSpan.FromSeconds(10),
                ct).ConfigureAwait(false);

            if (!httpResult.IsSuccess || httpResult.Data?.Artists == null || httpResult.Data.Artists.Count == 0)
            {
                return null;
            }

            var dto = httpResult.Data.Artists[0];
            return MapDtoToProfile(dto, musicBrainzArtistId);
        }).ConfigureAwait(false);
    }

    public async Task<EnrichedArtistProfile?> GetArtistProfileByNameAsync(
        string artistName,
        CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(artistName))
            return null;

        string inFlightKey = $"tadb_name:{artistName.Trim().ToLowerInvariant()}";

        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            string apiKey = _settingsService?.CurrentSettings.TheAudioDbApiKey?.Trim() ?? _options.ApiKey;
            string url = $"{_options.BaseUrl}/{apiKey}/search.php?s={Uri.EscapeDataString(artistName.Trim())}";

            var httpResult = await _httpService.GetJsonAsync<TadbArtistResponse>(
                url,
                ProviderKey,
                null,
                TimeSpan.FromSeconds(10),
                ct).ConfigureAwait(false);

            if (!httpResult.IsSuccess || httpResult.Data?.Artists == null || httpResult.Data.Artists.Count == 0)
            {
                return null;
            }

            var dto = httpResult.Data.Artists[0];
            return MapDtoToProfile(dto, dto.StrMusicBrainzID);
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> SearchArtistImageUrlsAsync(
        string artistName,
        ExternalIds? externalIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName) && string.IsNullOrWhiteSpace(externalIds?.MusicBrainzId))
            return Array.Empty<string>();

        EnrichedArtistProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(externalIds?.MusicBrainzId))
        {
            profile = await GetArtistProfileByMbidAsync(externalIds.MusicBrainzId, ct).ConfigureAwait(false);
        }

        if (profile == null && !string.IsNullOrWhiteSpace(artistName))
        {
            profile = await GetArtistProfileByNameAsync(artistName, ct).ConfigureAwait(false);
        }

        if (profile != null && profile.ExternalLinks != null)
        {
            var urls = new List<string>();
            if (profile.ExternalLinks.TryGetValue("ImageThumb", out var thumb) && !string.IsNullOrWhiteSpace(thumb))
                urls.Add(thumb);
            if (profile.ExternalLinks.TryGetValue("ImageFanart", out var fanart) && !string.IsNullOrWhiteSpace(fanart))
                urls.Add(fanart);

            return urls;
        }

        return Array.Empty<string>();
    }

    private static EnrichedArtistProfile MapDtoToProfile(TadbArtistDto dto, string? mbid)
    {
        string artistName = dto.StrArtist ?? "Unknown Artist";
        string artistId = !string.IsNullOrWhiteSpace(mbid) ? mbid : IdGenerator.FromArtist(artistName);

        int? formed = int.TryParse(dto.IntBornYear, out int b) && b > 0 ? b : null;
        int? died = int.TryParse(dto.IntDiedYear, out int d) && d > 0 ? d : null;

        var links = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(dto.StrWebsite)) links["Website"] = dto.StrWebsite;
        if (!string.IsNullOrWhiteSpace(dto.StrTwitter)) links["Twitter"] = dto.StrTwitter;
        if (!string.IsNullOrWhiteSpace(dto.StrFacebook)) links["Facebook"] = dto.StrFacebook;
        if (!string.IsNullOrWhiteSpace(dto.StrArtistThumb)) links["ImageThumb"] = dto.StrArtistThumb;
        if (!string.IsNullOrWhiteSpace(dto.StrArtistFanart)) links["ImageFanart"] = dto.StrArtistFanart;
        if (!string.IsNullOrWhiteSpace(dto.StrArtistLogo)) links["ImageLogo"] = dto.StrArtistLogo;

        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(dto.StrGenre)) tags.Add(dto.StrGenre);

        var extIds = new ExternalIds(
            MusicBrainzId: mbid ?? dto.StrMusicBrainzID,
            AdditionalIds: dto.IdArtist != null ? new Dictionary<string, string> { ["TheAudioDbId"] = dto.IdArtist } : null);

        return new EnrichedArtistProfile(
            ArtistId: artistId,
            Name: artistName,
            Biography: dto.StrBiographyEN,
            LocalImageToken: null, // Populated after local image download & caching
            PrimaryGenre: dto.StrGenre,
            Country: dto.StrCountry,
            FormedYear: formed,
            DisbandedYear: died,
            Tags: tags.Count > 0 ? tags : null,
            ExternalLinks: links.Count > 0 ? links : null,
            RelatedArtists: null,
            ExternalIds: extIds,
            ProviderName: "TheAudioDB");
    }
}
