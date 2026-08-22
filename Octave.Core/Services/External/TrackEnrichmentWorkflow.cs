using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Database;
using Octave.Core.Services.Metadata;
using Octave.Core.Services.Network;

namespace Octave.Core.Services.External;

public class TrackEnrichmentWorkflow : ITrackEnrichmentWorkflow
{
    private readonly SqliteDbContext _dbContext;
    private readonly ITrackMetadataMatcher _matcher;
    private readonly ITrackMetadataEditor _metadataEditor;
    private readonly IExternalAlbumArtworkProvider? _artworkProvider;
    private readonly IOnlineLyricsOrchestrator? _lyricsOrchestrator;
    private readonly IHttpService? _httpService;

    public TrackEnrichmentWorkflow(
        SqliteDbContext dbContext,
        ITrackMetadataMatcher matcher,
        ITrackMetadataEditor metadataEditor,
        IExternalAlbumArtworkProvider? artworkProvider = null,
        IOnlineLyricsOrchestrator? lyricsOrchestrator = null,
        IHttpService? httpService = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _metadataEditor = metadataEditor ?? throw new ArgumentNullException(nameof(metadataEditor));
        _artworkProvider = artworkProvider;
        _lyricsOrchestrator = lyricsOrchestrator;
        _httpService = httpService;
    }

    public async Task<TrackEnrichmentPlan> CreateEnrichmentPlanForTrackIdAsync(
        string trackId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            var dummy = new Track("", "", "", "", "", "", 0, "", "Local", 0, 0, DateTime.UtcNow);
            return new TrackEnrichmentPlan(dummy, MatchConfidenceTier.NoMatch, Array.Empty<CandidatePreview>(), null);
        }

        var track = await _dbContext.GetTrackByIdAsync(trackId).ConfigureAwait(false);
        if (track == null)
        {
            var dummy = new Track(trackId, "", "", "", "", "", 0, "", "Local", 0, 0, DateTime.UtcNow);
            return new TrackEnrichmentPlan(dummy, MatchConfidenceTier.NoMatch, Array.Empty<CandidatePreview>(), null);
        }

        return await CreateEnrichmentPlanAsync(track, ct).ConfigureAwait(false);
    }

    public async Task<TrackEnrichmentPlan> CreateEnrichmentPlanAsync(
        Track track,
        CancellationToken ct = default)
    {
        if (track == null)
        {
            var dummy = new Track("", "", "", "", "", "", 0, "", "Local", 0, 0, DateTime.UtcNow);
            return new TrackEnrichmentPlan(dummy, MatchConfidenceTier.NoMatch, Array.Empty<CandidatePreview>(), null);
        }

        // 1. Read Current File Tags & State
        string currentTitle = track.Title;
        string currentArtist = track.ArtistName;
        string currentAlbum = track.AlbumTitle;
        string? currentAlbumArtist = null;
        string? currentComposer = null;
        string? currentGenre = track.Genre;
        int? currentYear = track.Year > 0 ? track.Year : null;
        int? currentTrackNumber = track.TrackNumber > 0 ? track.TrackNumber : null;
        int? currentTrackCount = null;
        int? currentDiscNumber = null;
        int? currentDiscCount = null;
        string? currentComment = null;
        string? currentLyrics = null;
        string? currentArtworkUrl = null;
        ExternalIds currentExtIds = ExternalIds.Empty;

        if (!string.IsNullOrWhiteSpace(track.SourceUri) && File.Exists(track.SourceUri))
        {
            try
            {
                using (var tagFile = TagLib.File.Create(track.SourceUri))
                {
                    if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title)) currentTitle = tagFile.Tag.Title.Trim();
                    if (tagFile.Tag.Performers?.Length > 0 && !string.IsNullOrWhiteSpace(tagFile.Tag.Performers[0])) currentArtist = tagFile.Tag.Performers[0].Trim();
                    if (!string.IsNullOrWhiteSpace(tagFile.Tag.Album)) currentAlbum = tagFile.Tag.Album.Trim();
                    if (tagFile.Tag.AlbumArtists?.Length > 0 && !string.IsNullOrWhiteSpace(tagFile.Tag.AlbumArtists[0])) currentAlbumArtist = tagFile.Tag.AlbumArtists[0].Trim();
                    if (tagFile.Tag.Composers?.Length > 0 && !string.IsNullOrWhiteSpace(tagFile.Tag.Composers[0])) currentComposer = tagFile.Tag.Composers[0].Trim();
                    if (tagFile.Tag.Genres?.Length > 0 && !string.IsNullOrWhiteSpace(tagFile.Tag.Genres[0])) currentGenre = tagFile.Tag.Genres[0].Trim();
                    if (tagFile.Tag.Year > 0) currentYear = (int)tagFile.Tag.Year;
                    if (tagFile.Tag.Track > 0) currentTrackNumber = (int)tagFile.Tag.Track;
                    if (tagFile.Tag.TrackCount > 0) currentTrackCount = (int)tagFile.Tag.TrackCount;
                    if (tagFile.Tag.Disc > 0) currentDiscNumber = (int)tagFile.Tag.Disc;
                    if (tagFile.Tag.DiscCount > 0) currentDiscCount = (int)tagFile.Tag.DiscCount;
                    if (!string.IsNullOrWhiteSpace(tagFile.Tag.Comment)) currentComment = tagFile.Tag.Comment;
                    if (!string.IsNullOrWhiteSpace(tagFile.Tag.Lyrics)) currentLyrics = tagFile.Tag.Lyrics;

                    // Shared with matcher/scan so all consumers see identical local IDs.
                    currentExtIds = ExternalTagIds.Read(tagFile.Tag);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrackEnrichmentWorkflow] Tag read warning: {ex.Message}");
            }
        }

        // Query album record to see if artwork token exists
        var albumRecord = await _dbContext.GetAlbumByIdAsync(track.AlbumId).ConfigureAwait(false);
        currentArtworkUrl = albumRecord?.ArtworkUrl;

        // 2. Search & Match Candidate Metadata
        var matchCandidates = await _matcher.FindMatchesForTrackAsync(track, ct).ConfigureAwait(false);
        if (matchCandidates == null || matchCandidates.Count == 0)
        {
            return new TrackEnrichmentPlan(track, MatchConfidenceTier.NoMatch, Array.Empty<CandidatePreview>(), null);
        }

        // 3. Build Preview Models for each Candidate
        var previews = new List<CandidatePreview>();

        foreach (var c in matchCandidates)
        {
            if (ct.IsCancellationRequested) break;

            var tier = c.Confidence switch
            {
                >= 0.85 => MatchConfidenceTier.ExactMatch,
                >= 0.65 => MatchConfidenceTier.ProbableMatch,
                _ => MatchConfidenceTier.AmbiguousMatch
            };

            var meta = c.Metadata;

            // Fetch Candidate Artwork if available
            string? proposedArtworkUrl = null;
            byte[]? proposedArtworkBytes = null;
            string? proposedArtworkMime = null;

            if (_artworkProvider != null && _httpService != null)
            {
                try
                {
                    var artUrls = await _artworkProvider.SearchAlbumArtworkUrlsAsync(
                        meta.AlbumTitle ?? currentAlbum,
                        meta.ArtistName ?? currentArtist,
                        c.ExternalIds,
                        ct).ConfigureAwait(false);

                    if (artUrls != null && artUrls.Count > 0)
                    {
                        proposedArtworkUrl = artUrls[0];
                        var artHttpResult = await _httpService.GetByteArrayAsync(
                            proposedArtworkUrl,
                            _artworkProvider.ProviderName,
                            null,
                            TimeSpan.FromSeconds(5),
                            ct).ConfigureAwait(false);

                        if (artHttpResult.IsSuccess && artHttpResult.Data != null)
                        {
                            if (ImageValidator.IsValidImage(artHttpResult.Data, out string detectedMime))
                            {
                                proposedArtworkBytes = artHttpResult.Data;
                                proposedArtworkMime = detectedMime;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TrackEnrichmentWorkflow] Artwork retrieval failed for candidate '{c.ProviderName}': {ex.Message}");
                }
            }

            // Fetch Candidate Lyrics if available
            string? proposedLyrics = null;
            if (_lyricsOrchestrator != null)
            {
                try
                {
                    var lyricsResult = await _lyricsOrchestrator.FetchLyricsAsync(
                        meta.Title ?? currentTitle,
                        meta.ArtistName ?? currentArtist,
                        meta.AlbumTitle ?? currentAlbum,
                        meta.DurationSeconds ?? track.DurationSeconds,
                        c.ExternalIds,
                        ct).ConfigureAwait(false);

                    if (lyricsResult != null && lyricsResult.State != LyricsState.Unavailable)
                    {
                        proposedLyrics = lyricsResult.PlainText ?? (lyricsResult.SyncedLines != null ? string.Join(Environment.NewLine, lyricsResult.SyncedLines.Select(l => $"[{l.Start:mm\\:ss\\.ff}] {l.Text}")) : null);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TrackEnrichmentWorkflow] Lyrics retrieval failed for candidate '{c.ProviderName}': {ex.Message}");
                }
            }

            // Build Field Comparisons
            var titleComp = CreateComparison("Title", currentTitle, meta.Title);
            var artistComp = CreateComparison("Artist", currentArtist, meta.ArtistName);
            var albumComp = CreateComparison("Album", currentAlbum, meta.AlbumTitle);
            var albumArtistComp = CreateComparison("Album Artist", currentAlbumArtist, meta.ArtistName);
            var composerComp = CreateComparison("Composer", currentComposer, null);
            var genreComp = CreateComparison("Genre", currentGenre, meta.Genre);
            var yearComp = CreateNullableComparison("Year", currentYear, meta.Year);
            var trackNumComp = CreateNullableComparison("Track Number", currentTrackNumber, meta.TrackNumber);
            var trackCountComp = CreateNullableComparison("Track Count", currentTrackCount, null);
            var discNumComp = CreateNullableComparison("Disc Number", currentDiscNumber, meta.DiscNumber);
            var discCountComp = CreateNullableComparison("Disc Count", currentDiscCount, null);
            var lyricsComp = CreateComparison("Lyrics", currentLyrics, proposedLyrics);
            var artworkComp = CreateComparison("Artwork", currentArtworkUrl, proposedArtworkUrl);
            // Defensive local: providers construct candidates dynamically, so honor a
            // null despite the non-nullable declaration.
            ExternalIds? candidateIds = c.ExternalIds;

            var extIdsComp = new FieldComparison<ExternalIds>(
                "External IDs",
                currentExtIds,
                candidateIds,
                // ENR-01: ExternalIds now has value-based equality (dictionary members
                // used to compare by reference, flagging IDs as perpetually different
                // and rewriting them on every apply). Null proposed = nothing to apply.
                candidateIds != null && !currentExtIds.Equals(candidateIds),
                true);

            string candidateId = candidateIds?.MusicBrainzId ?? Guid.NewGuid().ToString("N");

            previews.Add(new CandidatePreview(
                candidateId,
                c.ProviderName,
                c.Confidence,
                tier,
                c.MatchEvidence,
                titleComp,
                artistComp,
                albumComp,
                albumArtistComp,
                composerComp,
                genreComp,
                yearComp,
                trackNumComp,
                trackCountComp,
                discNumComp,
                discCountComp,
                lyricsComp,
                artworkComp,
                extIdsComp,
                proposedArtworkBytes,
                proposedArtworkMime));
        }

        var sortedPreviews = previews.OrderByDescending(p => p.Confidence).ToList();
        var topTier = sortedPreviews.Count > 0 ? sortedPreviews[0].ConfidenceTier : MatchConfidenceTier.NoMatch;
        var bestCandidate = sortedPreviews.FirstOrDefault();

        return new TrackEnrichmentPlan(track, topTier, sortedPreviews, bestCandidate);
    }

    public async Task<EnrichmentApplyResult> ApplyEnrichmentAsync(
        EnrichmentApplyRequest request,
        CancellationToken ct = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.TrackId))
            return new EnrichmentApplyResult(false, MetadataEditResult.FileFailed(FileWriteResult.Failed("", "Track ID cannot be null or empty.")), Array.Empty<string>(), "Track ID missing.");

        var candidate = request.Candidate;
        var sel = request.Selection;
        var appliedFields = new List<string>();

        string? newTitle = null;
        if (sel.ApplyTitle && candidate.Title.HasDifference && !string.IsNullOrWhiteSpace(candidate.Title.ProposedValue))
        {
            newTitle = candidate.Title.ProposedValue;
            appliedFields.Add("Title");
        }

        string? newArtist = null;
        if (sel.ApplyArtist && candidate.Artist.HasDifference && !string.IsNullOrWhiteSpace(candidate.Artist.ProposedValue))
        {
            newArtist = candidate.Artist.ProposedValue;
            appliedFields.Add("Artist");
        }

        string? newAlbum = null;
        if (sel.ApplyAlbum && candidate.Album.HasDifference && !string.IsNullOrWhiteSpace(candidate.Album.ProposedValue))
        {
            newAlbum = candidate.Album.ProposedValue;
            appliedFields.Add("Album");
        }

        string? newAlbumArtist = null;
        if (sel.ApplyAlbumArtist && candidate.AlbumArtist.HasDifference && !string.IsNullOrWhiteSpace(candidate.AlbumArtist.ProposedValue))
        {
            newAlbumArtist = candidate.AlbumArtist.ProposedValue;
            appliedFields.Add("AlbumArtist");
        }

        string? newComposer = null;
        if (sel.ApplyComposer && candidate.Composer.HasDifference && !string.IsNullOrWhiteSpace(candidate.Composer.ProposedValue))
        {
            newComposer = candidate.Composer.ProposedValue;
            appliedFields.Add("Composer");
        }

        string? newGenre = null;
        if (sel.ApplyGenre && candidate.Genre.HasDifference && !string.IsNullOrWhiteSpace(candidate.Genre.ProposedValue))
        {
            newGenre = candidate.Genre.ProposedValue;
            appliedFields.Add("Genre");
        }

        int? newYear = null;
        if (sel.ApplyYear && candidate.Year.HasDifference && candidate.Year.ProposedValue.HasValue)
        {
            newYear = candidate.Year.ProposedValue;
            appliedFields.Add("Year");
        }

        int? newTrackNum = null;
        if (sel.ApplyTrackNumber && candidate.TrackNumber.HasDifference && candidate.TrackNumber.ProposedValue.HasValue)
        {
            newTrackNum = candidate.TrackNumber.ProposedValue;
            appliedFields.Add("TrackNumber");
        }

        int? newTrackCount = null;
        if (sel.ApplyTrackCount && candidate.TrackCount.HasDifference && candidate.TrackCount.ProposedValue.HasValue)
        {
            newTrackCount = candidate.TrackCount.ProposedValue;
            appliedFields.Add("TrackCount");
        }

        int? newDiscNum = null;
        if (sel.ApplyDiscNumber && candidate.DiscNumber.HasDifference && candidate.DiscNumber.ProposedValue.HasValue)
        {
            newDiscNum = candidate.DiscNumber.ProposedValue;
            appliedFields.Add("DiscNumber");
        }

        int? newDiscCount = null;
        if (sel.ApplyDiscCount && candidate.DiscCount.HasDifference && candidate.DiscCount.ProposedValue.HasValue)
        {
            newDiscCount = candidate.DiscCount.ProposedValue;
            appliedFields.Add("DiscCount");
        }

        string? newLyrics = null;
        if (sel.ApplyLyrics && candidate.Lyrics.HasDifference && !string.IsNullOrWhiteSpace(candidate.Lyrics.ProposedValue))
        {
            newLyrics = candidate.Lyrics.ProposedValue;
            appliedFields.Add("Lyrics");
        }

        byte[]? newArtworkBytes = null;
        string? newArtworkMime = null;
        if (sel.ApplyArtwork && candidate.ProposedArtworkBytes != null && candidate.ProposedArtworkBytes.Length > 0)
        {
            newArtworkBytes = candidate.ProposedArtworkBytes;
            newArtworkMime = candidate.ProposedArtworkMime;
            appliedFields.Add("Artwork");
        }

        ExternalIds? newExtIds = null;
        if (sel.ApplyExternalIds && candidate.ExternalIds.HasDifference && candidate.ExternalIds.ProposedValue != null)
        {
            newExtIds = candidate.ExternalIds.ProposedValue;
            appliedFields.Add("ExternalIds");
        }

        var update = new TrackMetadataUpdate(
            Title: newTitle,
            ArtistName: newArtist,
            AlbumTitle: newAlbum,
            AlbumArtist: newAlbumArtist,
            Composer: newComposer,
            Genre: newGenre,
            Year: newYear,
            TrackNumber: newTrackNum,
            TrackCount: newTrackCount,
            DiscNumber: newDiscNum,
            DiscCount: newDiscCount,
            Lyrics: newLyrics,
            NewArtworkBytes: newArtworkBytes,
            ArtworkMimeType: newArtworkMime,
            ClearArtwork: false,
            ExternalIds: newExtIds);

        var editResult = await _metadataEditor.UpdateTrackMetadataAsync(request.TrackId, update, ct).ConfigureAwait(false);

        return new EnrichmentApplyResult(
            editResult.Success,
            editResult,
            appliedFields.AsReadOnly(),
            editResult.SummaryMessage);
    }

    private static FieldComparison<string> CreateComparison(string fieldName, string? current, string? proposed)
    {
        string? c = !string.IsNullOrWhiteSpace(current) ? current.Trim() : null;
        string? p = !string.IsNullOrWhiteSpace(proposed) ? proposed.Trim() : null;
        bool diff = !string.Equals(c, p, StringComparison.OrdinalIgnoreCase) && p != null;

        return new FieldComparison<string>(fieldName, c, p, diff, diff);
    }

    private static FieldComparison<int?> CreateNullableComparison(string fieldName, int? current, int? proposed)
    {
        bool diff = proposed.HasValue && proposed.Value > 0 && proposed != current;
        return new FieldComparison<int?>(fieldName, current, proposed, diff, diff);
    }
}
