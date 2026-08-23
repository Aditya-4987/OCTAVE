using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.External.Settings;
using Octave.Core.Services.Network;

namespace Octave.Core.Services.External.MusicBrainz;

public class MusicBrainzMetadataProvider : IExternalMetadataProvider
{
    private const string BaseUrl = "https://musicbrainz.org/ws/2";
    private const string ProviderKey = "musicbrainz";
    private readonly IHttpService _httpService;
    private readonly IExternalDataSettingsService? _settingsService;
    private readonly AsyncSingleFlight _singleFlight = new();
    private bool _isEnabled = true;

    public string ProviderName => "MusicBrainz";
    public bool IsEnabled
    {
        get => _settingsService != null ? (_settingsService.CurrentSettings.MusicBrainzEnabled && _settingsService.CurrentSettings.EnableOnlineMetadata && !_settingsService.CurrentSettings.OfflineOnlyMode) : _isEnabled;
        set => _isEnabled = value;
    }
    public int Priority { get; set; } = 10;

    public MusicBrainzMetadataProvider(
        IHttpService httpService,
        IProviderRateLimiterRegistry? rateLimiterRegistry = null,
        IExternalDataSettingsService? settingsService = null)
    {
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
        _settingsService = settingsService;
        // MusicBrainz policy: 1 request per second
        rateLimiterRegistry?.GetOrCreate(ProviderKey, TimeSpan.FromSeconds(1));
    }

    public async Task<IReadOnlyList<TrackMatchCandidate>> SearchTrackCandidatesAsync(
        string title,
        string artist,
        string? album = null,
        double? durationSeconds = null,
        CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return Array.Empty<TrackMatchCandidate>();
        }

        string query = BuildRecordingQuery(title, artist, album);
        string requestUrl = $"{BaseUrl}/recording?query={Uri.EscapeDataString(query)}&limit=10&fmt=json";
        string inFlightKey = $"search_track:{requestUrl}";

        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var httpResult = await _httpService.GetJsonAsync<MbRecordingSearchResponse>(
                requestUrl,
                ProviderKey,
                null,
                TimeSpan.FromSeconds(12),
                ct).ConfigureAwait(false);

            if (!httpResult.IsSuccess || httpResult.Data?.Recordings == null || httpResult.Data.Recordings.Count == 0)
            {
                return (IReadOnlyList<TrackMatchCandidate>)Array.Empty<TrackMatchCandidate>();
            }

            var candidates = new List<TrackMatchCandidate>();
            foreach (var rec in httpResult.Data.Recordings)
            {
                if (string.IsNullOrWhiteSpace(rec.Id) || string.IsNullOrWhiteSpace(rec.Title))
                    continue;

                string artistName = FormatArtistCredit(rec.ArtistCredit) ?? artist;
                var firstRelease = rec.Releases?.FirstOrDefault();
                string? albumTitle = firstRelease?.Title ?? album;
                int? year = ExtractYear(rec.FirstReleaseDate ?? firstRelease?.Date);
                string? genre = rec.Genres?.FirstOrDefault()?.Name ?? rec.Tags?.FirstOrDefault()?.Name;

                // MB-03: the track#/disc# must describe THE MATCHED RECORDING's
                // slot, not "first medium, first track of the first release"
                // (which stamped every candidate with arbitrary numbers).
                var (trackNumber, discNumber) = LocateTrackPosition(rec);

                double? duration = rec.LengthMs.HasValue ? rec.LengthMs.Value / 1000.0 : null;
                string? isrc = rec.Isrcs?.FirstOrDefault();

                var additionalIds = new Dictionary<string, string>();
                if (!string.IsNullOrWhiteSpace(firstRelease?.Id))
                    additionalIds["MusicBrainzReleaseId"] = firstRelease.Id;
                if (!string.IsNullOrWhiteSpace(firstRelease?.ReleaseGroup?.Id))
                    additionalIds["MusicBrainzReleaseGroupId"] = firstRelease.ReleaseGroup.Id;
                if (rec.ArtistCredit?.FirstOrDefault()?.Artist?.Id is string artMbid && !string.IsNullOrWhiteSpace(artMbid))
                    additionalIds["MusicBrainzArtistId"] = artMbid;
                additionalIds[ExternalIdKinds.EntityKind] = ExternalIdKinds.KindRecording;

                var externalIds = new ExternalIds(
                    MusicBrainzId: rec.Id,
                    Isrc: isrc,
                    AdditionalIds: additionalIds.Count > 0 ? additionalIds : null);

                var metadata = new ExternalTrackMetadata(
                    Title: rec.Title,
                    ArtistName: artistName,
                    AlbumTitle: albumTitle,
                    Year: year,
                    Genre: genre,
                    TrackNumber: trackNumber,
                    DiscNumber: discNumber,
                    DurationSeconds: duration,
                    Isrc: isrc,
                    ExternalIds: externalIds);

                double confidence = CalculateTrackConfidence(rec, title, artist, album, durationSeconds);
                string evidence = $"MusicBrainz Score: {rec.Score ?? 0}%, matched '{rec.Title}' by '{artistName}'";
                if (duration.HasValue && durationSeconds.HasValue)
                {
                    double diff = Math.Abs(duration.Value - durationSeconds.Value);
                    evidence += $", duration diff: {diff:0.#}s";
                }

                candidates.Add(new TrackMatchCandidate(ProviderName, externalIds, confidence, evidence, metadata));
            }

            return (IReadOnlyList<TrackMatchCandidate>)candidates.OrderByDescending(c => c.Confidence).ToList();
        }).ConfigureAwait(false) ?? Array.Empty<TrackMatchCandidate>();
    }

    public async Task<IReadOnlyList<AlbumMatchCandidate>> SearchAlbumCandidatesAsync(
        string albumTitle,
        string artistName,
        int? year = null,
        CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(albumTitle) || string.IsNullOrWhiteSpace(artistName))
        {
            return Array.Empty<AlbumMatchCandidate>();
        }

        string query = $"release:\"{EscapeLucene(albumTitle)}\" AND artist:\"{EscapeLucene(artistName)}\"";
        if (year.HasValue && year.Value > 0)
        {
            query += $" AND date:{year.Value}";
        }

        string requestUrl = $"{BaseUrl}/release?query={Uri.EscapeDataString(query)}&limit=10&fmt=json";
        string inFlightKey = $"search_album:{requestUrl}";

        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var httpResult = await _httpService.GetJsonAsync<MbReleaseSearchResponse>(
                requestUrl,
                ProviderKey,
                null,
                TimeSpan.FromSeconds(12),
                ct).ConfigureAwait(false);

            if (!httpResult.IsSuccess || httpResult.Data?.Releases == null || httpResult.Data.Releases.Count == 0)
            {
                return (IReadOnlyList<AlbumMatchCandidate>)Array.Empty<AlbumMatchCandidate>();
            }

            var candidates = new List<AlbumMatchCandidate>();
            foreach (var rel in httpResult.Data.Releases)
            {
                if (string.IsNullOrWhiteSpace(rel.Id) || string.IsNullOrWhiteSpace(rel.Title))
                    continue;

                string artist = FormatArtistCredit(rel.ArtistCredit) ?? artistName;
                int? relYear = ExtractYear(rel.Date);
                string? genre = rel.Genres?.FirstOrDefault()?.Name ?? rel.Tags?.FirstOrDefault()?.Name;
                int? trackCount = rel.TrackCount ?? rel.Media?.Sum(m => m.TrackCount ?? 0);

                var additionalIds = new Dictionary<string, string>();
                if (!string.IsNullOrWhiteSpace(rel.ReleaseGroup?.Id))
                    additionalIds["MusicBrainzReleaseGroupId"] = rel.ReleaseGroup.Id;
                if (rel.ArtistCredit?.FirstOrDefault()?.Artist?.Id is string artMbid && !string.IsNullOrWhiteSpace(artMbid))
                    additionalIds["MusicBrainzArtistId"] = artMbid;
                additionalIds[ExternalIdKinds.EntityKind] = ExternalIdKinds.KindRelease;

                var externalIds = new ExternalIds(
                    MusicBrainzId: rel.Id,
                    AdditionalIds: additionalIds.Count > 0 ? additionalIds : null);

                var metadata = new ExternalAlbumMetadata(
                    Title: rel.Title,
                    ArtistName: artist,
                    Year: relYear,
                    ReleaseDate: rel.Date,
                    Genre: genre,
                    TotalTracks: trackCount,
                    ExternalIds: externalIds);

                double confidence = CalculateAlbumConfidence(rel, albumTitle, artistName, year);
                string evidence = $"MusicBrainz Release Score: {rel.Score ?? 0}%, date: '{rel.Date}', status: '{rel.Status}'";

                candidates.Add(new AlbumMatchCandidate(ProviderName, externalIds, confidence, evidence, metadata));
            }

            return (IReadOnlyList<AlbumMatchCandidate>)candidates.OrderByDescending(c => c.Confidence).ToList();
        }).ConfigureAwait(false) ?? Array.Empty<AlbumMatchCandidate>();
    }

    public async Task<IReadOnlyList<ArtistMatchCandidate>> SearchArtistCandidatesAsync(
        string artistName,
        CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(artistName))
        {
            return Array.Empty<ArtistMatchCandidate>();
        }

        string query = $"artist:\"{EscapeLucene(artistName)}\"";
        string requestUrl = $"{BaseUrl}/artist?query={Uri.EscapeDataString(query)}&limit=10&fmt=json";
        string inFlightKey = $"search_artist:{requestUrl}";

        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var httpResult = await _httpService.GetJsonAsync<MbArtistSearchResponse>(
                requestUrl,
                ProviderKey,
                null,
                TimeSpan.FromSeconds(12),
                ct).ConfigureAwait(false);

            if (!httpResult.IsSuccess || httpResult.Data?.Artists == null || httpResult.Data.Artists.Count == 0)
            {
                return (IReadOnlyList<ArtistMatchCandidate>)Array.Empty<ArtistMatchCandidate>();
            }

            var candidates = new List<ArtistMatchCandidate>();
            foreach (var art in httpResult.Data.Artists)
            {
                if (string.IsNullOrWhiteSpace(art.Id) || string.IsNullOrWhiteSpace(art.Name))
                    continue;

                int? formed = ExtractYear(art.LifeSpan?.Begin);
                int? disbanded = art.LifeSpan?.Ended == true ? ExtractYear(art.LifeSpan?.End) : null;

                var externalIds = new ExternalIds(MusicBrainzId: art.Id);
                var metadata = new ExternalArtistMetadata(
                    Name: art.Name,
                    Bio: art.Disambiguation,
                    Country: art.Country,
                    FormedYear: formed,
                    DisbandedYear: disbanded,
                    ExternalIds: externalIds);

                double confidence = CalculateArtistConfidence(art, artistName);
                string evidence = $"MusicBrainz Artist Score: {art.Score ?? 0}%, type: '{art.Type}', country: '{art.Country}'";

                candidates.Add(new ArtistMatchCandidate(ProviderName, externalIds, confidence, evidence, metadata));
            }

            return (IReadOnlyList<ArtistMatchCandidate>)candidates.OrderByDescending(c => c.Confidence).ToList();
        }).ConfigureAwait(false) ?? Array.Empty<ArtistMatchCandidate>();
    }

    public async Task<ExternalTrackMetadata?> GetTrackMetadataAsync(
        string providerEntityId,
        CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(providerEntityId))
            return null;

        string requestUrl = $"{BaseUrl}/recording/{Uri.EscapeDataString(providerEntityId)}?inc=artist-credits+releases+isrcs+genres+tags&fmt=json";
        string inFlightKey = $"get_track:{requestUrl}";

        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var httpResult = await _httpService.GetJsonAsync<MbRecordingDto>(
                requestUrl,
                ProviderKey,
                null,
                TimeSpan.FromSeconds(12),
                ct).ConfigureAwait(false);

            if (!httpResult.IsSuccess || httpResult.Data == null)
            {
                return null;
            }

            var rec = httpResult.Data;
            string artistName = FormatArtistCredit(rec.ArtistCredit) ?? "Unknown Artist";
            var firstRelease = rec.Releases?.FirstOrDefault();
            string? albumTitle = firstRelease?.Title;
            int? year = ExtractYear(rec.FirstReleaseDate ?? firstRelease?.Date);
            string? genre = rec.Genres?.FirstOrDefault()?.Name ?? rec.Tags?.FirstOrDefault()?.Name;
            double? duration = rec.LengthMs.HasValue ? rec.LengthMs.Value / 1000.0 : null;
            string? isrc = rec.Isrcs?.FirstOrDefault();

            var additionalIds = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(firstRelease?.Id))
                additionalIds["MusicBrainzReleaseId"] = firstRelease.Id;
            if (!string.IsNullOrWhiteSpace(firstRelease?.ReleaseGroup?.Id))
                additionalIds["MusicBrainzReleaseGroupId"] = firstRelease.ReleaseGroup.Id;
            if (rec.ArtistCredit?.FirstOrDefault()?.Artist?.Id is string artMbid && !string.IsNullOrWhiteSpace(artMbid))
                additionalIds["MusicBrainzArtistId"] = artMbid;
            additionalIds[ExternalIdKinds.EntityKind] = ExternalIdKinds.KindRecording;

            var externalIds = new ExternalIds(
                MusicBrainzId: rec.Id,
                Isrc: isrc,
                AdditionalIds: additionalIds.Count > 0 ? additionalIds : null);

            return new ExternalTrackMetadata(
                Title: rec.Title,
                ArtistName: artistName,
                AlbumTitle: albumTitle,
                Year: year,
                Genre: genre,
                TrackNumber: null,
                DiscNumber: null,
                DurationSeconds: duration,
                Isrc: isrc,
                ExternalIds: externalIds);
        }).ConfigureAwait(false);
    }

    public async Task<ExternalAlbumMetadata?> GetAlbumMetadataAsync(
        string providerEntityId,
        CancellationToken ct = default)
    {
        // MB-01: the lookup paths must honor IsEnabled like the search paths —
        // a disabled provider must never touch the network.
        if (!IsEnabled || string.IsNullOrWhiteSpace(providerEntityId))
            return null;

        string requestUrl = $"{BaseUrl}/release/{Uri.EscapeDataString(providerEntityId)}?inc=artist-credits+recordings+genres+tags+release-groups+media&fmt=json";
        string inFlightKey = $"get_album:{requestUrl}";

        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var httpResult = await _httpService.GetJsonAsync<MbReleaseDto>(
                requestUrl,
                ProviderKey,
                null,
                TimeSpan.FromSeconds(12),
                ct).ConfigureAwait(false);

            if (!httpResult.IsSuccess || httpResult.Data == null)
            {
                return null;
            }

            var rel = httpResult.Data;
            string artist = FormatArtistCredit(rel.ArtistCredit) ?? "Unknown Artist";
            int? year = ExtractYear(rel.Date);
            string? genre = rel.Genres?.FirstOrDefault()?.Name ?? rel.Tags?.FirstOrDefault()?.Name;
            int? trackCount = rel.TrackCount ?? rel.Media?.Sum(m => m.TrackCount ?? 0);

            var additionalIds = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(rel.ReleaseGroup?.Id))
                additionalIds["MusicBrainzReleaseGroupId"] = rel.ReleaseGroup.Id;
            if (rel.ArtistCredit?.FirstOrDefault()?.Artist?.Id is string artMbid && !string.IsNullOrWhiteSpace(artMbid))
                additionalIds["MusicBrainzArtistId"] = artMbid;
            additionalIds[ExternalIdKinds.EntityKind] = ExternalIdKinds.KindRelease;

            var externalIds = new ExternalIds(
                MusicBrainzId: rel.Id,
                AdditionalIds: additionalIds.Count > 0 ? additionalIds : null);

            var tracklist = new List<ExternalTrackMetadata>();
            if (rel.Media != null)
            {
                foreach (var medium in rel.Media)
                {
                    if (medium.Tracks == null) continue;
                    foreach (var trk in medium.Tracks)
                    {
                        if (string.IsNullOrWhiteSpace(trk.Title)) continue;

                        int? tNum = int.TryParse(trk.Number, out int n) ? n : null;
                        double? tDur = trk.LengthMs.HasValue ? trk.LengthMs.Value / 1000.0 : null;
                        string trkArtist = trk.Recording != null ? FormatArtistCredit(trk.Recording.ArtistCredit) ?? artist : artist;

                        var trkExtIds = new ExternalIds(MusicBrainzId: trk.Recording?.Id ?? trk.Id);

                        tracklist.Add(new ExternalTrackMetadata(
                            Title: trk.Title,
                            ArtistName: trkArtist,
                            AlbumTitle: rel.Title,
                            Year: year,
                            Genre: genre,
                            TrackNumber: tNum,
                            DiscNumber: medium.Position,
                            DurationSeconds: tDur,
                            Isrc: trk.Recording?.Isrcs?.FirstOrDefault(),
                            ExternalIds: trkExtIds));
                    }
                }
            }

            return new ExternalAlbumMetadata(
                Title: rel.Title,
                ArtistName: artist,
                Year: year,
                ReleaseDate: rel.Date,
                Genre: genre,
                TotalTracks: trackCount,
                ExternalIds: externalIds,
                Tracklist: tracklist.Count > 0 ? tracklist : null);
        }).ConfigureAwait(false);
    }

    public async Task<ExternalArtistMetadata?> GetArtistMetadataAsync(
        string providerEntityId,
        CancellationToken ct = default)
    {
        // MB-01: same disabled-provider guard as the other lookups.
        if (!IsEnabled || string.IsNullOrWhiteSpace(providerEntityId))
            return null;

        string requestUrl = $"{BaseUrl}/artist/{Uri.EscapeDataString(providerEntityId)}?inc=genres+tags&fmt=json";
        string inFlightKey = $"get_artist:{requestUrl}";

        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var httpResult = await _httpService.GetJsonAsync<MbArtistDto>(
                requestUrl,
                ProviderKey,
                null,
                TimeSpan.FromSeconds(12),
                ct).ConfigureAwait(false);

            if (!httpResult.IsSuccess || httpResult.Data == null)
            {
                return null;
            }

            var art = httpResult.Data;
            int? formed = ExtractYear(art.LifeSpan?.Begin);
            int? disbanded = art.LifeSpan?.Ended == true ? ExtractYear(art.LifeSpan?.End) : null;

            var externalIds = new ExternalIds(MusicBrainzId: art.Id);
            return new ExternalArtistMetadata(
                Name: art.Name,
                Bio: art.Disambiguation,
                Country: art.Country,
                FormedYear: formed,
                DisbandedYear: disbanded,
                ExternalIds: externalIds);
        }).ConfigureAwait(false);
    }

    public async Task<ExternalAlbumMetadata?> GetReleaseGroupMetadataAsync(
        string releaseGroupId,
        CancellationToken ct = default)
    {
        // MB-01: same disabled-provider guard as the other lookups.
        if (!IsEnabled || string.IsNullOrWhiteSpace(releaseGroupId))
            return null;

        string requestUrl = $"{BaseUrl}/release-group/{Uri.EscapeDataString(releaseGroupId)}?inc=artist-credits+releases+genres+tags&fmt=json";
        string inFlightKey = $"get_release_group:{requestUrl}";

        return await _singleFlight.ExecuteAsync(inFlightKey, async () =>
        {
            var httpResult = await _httpService.GetJsonAsync<MbReleaseGroupDto>(
                requestUrl,
                ProviderKey,
                null,
                TimeSpan.FromSeconds(12),
                ct).ConfigureAwait(false);

            if (!httpResult.IsSuccess || httpResult.Data == null)
            {
                return null;
            }

            var rg = httpResult.Data;
            string artist = FormatArtistCredit(rg.ArtistCredit) ?? "Unknown Artist";
            int? year = ExtractYear(rg.FirstReleaseDate);
            string? genre = rg.Genres?.FirstOrDefault()?.Name ?? rg.Tags?.FirstOrDefault()?.Name;

            var externalIds = new ExternalIds(
                MusicBrainzId: rg.Id,
                AdditionalIds: new Dictionary<string, string>
                {
                    ["PrimaryType"] = rg.PrimaryType ?? "Album",
                    [ExternalIdKinds.EntityKind] = ExternalIdKinds.KindReleaseGroup
                });

            return new ExternalAlbumMetadata(
                Title: rg.Title,
                ArtistName: artist,
                Year: year,
                ReleaseDate: rg.FirstReleaseDate,
                Genre: genre,
                TotalTracks: null,
                ExternalIds: externalIds);
        }).ConfigureAwait(false);
    }

    // =================================================================
    // HELPER METHODS
    // =================================================================

    private static string BuildRecordingQuery(string title, string artist, string? album)
    {
        string q = $"recording:\"{EscapeLucene(title)}\" AND artist:\"{EscapeLucene(artist)}\"";
        if (!string.IsNullOrWhiteSpace(album))
        {
            q += $" AND release:\"{EscapeLucene(album)}\"";
        }
        return q;
    }

    private static string EscapeLucene(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        // Escape Lucene special characters: + - && || ! ( ) { } [ ] ^ " ~ * ? : \ /
        return Regex.Replace(text, @"([+\-&|!(){}\[\]^""~*?:\\/])", @"\$1");
    }

    private static string? FormatArtistCredit(List<MbArtistCreditDto>? credits)
    {
        if (credits == null || credits.Count == 0) return null;
        var parts = new List<string>();
        foreach (var c in credits)
        {
            string name = !string.IsNullOrWhiteSpace(c.Name) ? c.Name : (c.Artist?.Name ?? "");
            parts.Add(name + (c.JoinPhrase ?? ""));
        }
        return string.Join("", parts).Trim();
    }

    private static int? ExtractYear(string? dateString)
    {
        if (string.IsNullOrWhiteSpace(dateString) || dateString.Length < 4) return null;
        if (int.TryParse(dateString.AsSpan(0, 4), out int year) && year > 1000 && year < 3000)
        {
            return year;
        }
        return null;
    }

    // MB-03: find the matched recording's real slot — the medium whose track
    // list contains a track pointing at THIS recording id, scanning releases
    // in listing order. Returns (null, null) when no media/track data names
    // the recording, so callers leave the numbers unknown instead of
    // inventing "first medium, first track".
    private static (int? TrackNumber, int? DiscNumber) LocateTrackPosition(MbRecordingDto rec)
    {
        if (rec.Releases == null || string.IsNullOrWhiteSpace(rec.Id))
            return (null, null);

        foreach (var release in rec.Releases)
        {
            if (release.Media == null) continue;
            foreach (var medium in release.Media)
            {
                var hit = medium.Tracks?.FirstOrDefault(t =>
                    t.Recording != null &&
                    string.Equals(t.Recording.Id, rec.Id, StringComparison.OrdinalIgnoreCase));

                if (hit != null)
                {
                    int? number = int.TryParse(hit.Number, out int n) && n > 0 ? n : null;
                    return (number, medium.Position);
                }
            }
        }

        return (null, null);
    }

    private static double CalculateTrackConfidence(MbRecordingDto rec, string targetTitle, string targetArtist, string? targetAlbum, double? targetDuration)
    {
        double baseScore = (rec.Score ?? 50) / 100.0;

        // Exact title match boost
        if (rec.Title.Equals(targetTitle, StringComparison.OrdinalIgnoreCase))
        {
            baseScore = Math.Max(baseScore, 0.85);
        }

        // MB-02: the search-engine score and a title hit say nothing about WHO
        // performs the result. Fold the normalized artist comparison in so a
        // same-titled recording by a different artist (cover/tribute) cannot
        // ride its search score into an auto-apply tier.
        string recArtist = FormatArtistCredit(rec.ArtistCredit) ?? string.Empty;
        double artistSim = MetadataTextNormalizer.CalculateSimilarity(targetArtist, recArtist);
        baseScore *= 0.5 + 0.5 * artistSim;
        if (artistSim >= 0.95)
        {
            baseScore = Math.Min(1.0, baseScore + 0.05);
        }

        // MB-02: same guard for the release — matching title+artist on a
        // clearly different album is more often a different edit than a hit.
        string? recAlbum = rec.Releases?.FirstOrDefault()?.Title;
        if (!string.IsNullOrWhiteSpace(targetAlbum) && !string.IsNullOrWhiteSpace(recAlbum))
        {
            double albumSim = MetadataTextNormalizer.CalculateSimilarity(targetAlbum, recAlbum);
            if (albumSim >= 0.85)
            {
                baseScore = Math.Min(1.0, baseScore + 0.05);
            }
            else if (albumSim < 0.40)
            {
                baseScore -= 0.10;
            }
        }

        // Duration check
        if (targetDuration.HasValue && rec.LengthMs.HasValue)
        {
            double recDurSec = rec.LengthMs.Value / 1000.0;
            double diff = Math.Abs(recDurSec - targetDuration.Value);
            if (diff <= 2.0)
            {
                baseScore = Math.Min(1.0, baseScore + 0.10);
            }
            else if (diff > 15.0)
            {
                baseScore = Math.Max(0.1, baseScore - 0.20);
            }
        }

        return Math.Round(Math.Clamp(baseScore, 0.0, 1.0), 2);
    }

    private static double CalculateAlbumConfidence(MbReleaseDto rel, string targetAlbum, string targetArtist, int? targetYear)
    {
        double baseScore = (rel.Score ?? 50) / 100.0;

        if (rel.Title.Equals(targetAlbum, StringComparison.OrdinalIgnoreCase))
        {
            baseScore = Math.Max(baseScore, 0.85);
        }

        // MB-02: an album title is not unique across artists — weigh the
        // performer in before a wrong-artist same-named release can reach
        // auto-apply confidence.
        string relArtist = FormatArtistCredit(rel.ArtistCredit) ?? string.Empty;
        double artistSim = MetadataTextNormalizer.CalculateSimilarity(targetArtist, relArtist);
        baseScore *= 0.5 + 0.5 * artistSim;
        if (artistSim >= 0.95)
        {
            baseScore = Math.Min(1.0, baseScore + 0.05);
        }

        if (targetYear.HasValue && ExtractYear(rel.Date) == targetYear.Value)
        {
            baseScore = Math.Min(1.0, baseScore + 0.10);
        }

        return Math.Round(Math.Clamp(baseScore, 0.0, 1.0), 2);
    }

    private static double CalculateArtistConfidence(MbArtistDto art, string targetArtist)
    {
        double baseScore = (art.Score ?? 50) / 100.0;

        if (art.Name.Equals(targetArtist, StringComparison.OrdinalIgnoreCase))
        {
            baseScore = Math.Max(baseScore, 0.95);
        }

        return Math.Round(Math.Clamp(baseScore, 0.0, 1.0), 2);
    }
}
