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
        LocalTagState current = await ReadLocalTagStateAsync(track).ConfigureAwait(false);

        // Query album record to see if artwork token exists (folded into the snapshot).
        var albumRecord = await _dbContext.GetAlbumByIdAsync(track.AlbumId).ConfigureAwait(false);
        current = current with { ArtworkUrl = albumRecord?.ArtworkUrl };

        // 2. Search & Match Candidate Metadata
        var matchCandidates = await _matcher.FindMatchesForTrackAsync(track, ct).ConfigureAwait(false);
        if (matchCandidates == null || matchCandidates.Count == 0)
        {
            return new TrackEnrichmentPlan(track, MatchConfidenceTier.NoMatch, Array.Empty<CandidatePreview>(), null);
        }

        // 3. Build Preview Models for each Candidate.
        // ENR-02: candidate artwork (an HTTP search plus a 5 s image download) and
        // lyrics used to be fetched SEQUENTIALLY FOR EVERY CANDIDATE here, even though
        // only one candidate can ever be applied — N candidates meant N round-trip
        // chains and provider rate-limit pressure before the dialog even opened.
        // Previews are now built metadata-only; the top candidate is hydrated below,
        // and any other candidate can be hydrated on demand via HydrateCandidateAsync.
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

            var titleComp = CreateComparison("Title", current.Title, meta.Title);
            var artistComp = CreateComparison("Artist", current.ArtistName, meta.ArtistName);
            var albumComp = CreateComparison("Album", current.AlbumTitle, meta.AlbumTitle);
            // ENR-03: the "Album Artist" row proposed the candidate's TRACK artist
            // (ExternalTrackMetadata has no album-artist field), which is wrong for
            // compilations / featured-artist tracks. Leave it unset: show the local
            // value, propose nothing.
            var albumArtistComp = CreateComparison("Album Artist", current.AlbumArtist, null);
            var composerComp = CreateComparison("Composer", current.Composer, null);
            var genreComp = CreateComparison("Genre", current.Genre, meta.Genre);
            var yearComp = CreateNullableComparison("Year", current.Year, meta.Year);
            var trackNumComp = CreateNullableComparison("Track Number", current.TrackNumber, meta.TrackNumber);
            var trackCountComp = CreateNullableComparison("Track Count", current.TrackCount, null);
            var discNumComp = CreateNullableComparison("Disc Number", current.DiscNumber, meta.DiscNumber);
            var discCountComp = CreateNullableComparison("Disc Count", current.DiscCount, null);
            // No artwork/lyrics fetch here (ENR-02): both comparisons are hydrated per
            // candidate on demand; the placeholder rows simply propose nothing.
            var lyricsComp = CreateComparison("Lyrics", current.Lyrics, null);
            var artworkComp = CreateComparison("Artwork", current.ArtworkUrl, null);
            // Defensive local: providers construct candidates dynamically, so honor a
            // null despite the non-nullable declaration.
            ExternalIds? candidateIds = c.ExternalIds;

            var extIdsComp = new FieldComparison<ExternalIds>(
                "External IDs",
                current.ExternalIds,
                candidateIds,
                // ENR-01: ExternalIds now has value-based equality (dictionary members
                // used to compare by reference, flagging IDs as perpetually different
                // and rewriting them on every apply). Null proposed = nothing to apply.
                candidateIds != null && !current.ExternalIds.Equals(candidateIds),
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
                extIdsComp));
        }

        var sortedPreviews = previews.OrderByDescending(p => p.Confidence).ToList();
        var topTier = sortedPreviews.Count > 0 ? sortedPreviews[0].ConfidenceTier : MatchConfidenceTier.NoMatch;
        var bestCandidate = sortedPreviews.FirstOrDefault();

        // ENR-02: hydrate ONLY the top candidate — the one the dialog opens selected.
        if (bestCandidate != null && !ct.IsCancellationRequested)
        {
            try
            {
                CandidatePreview hydrated = await HydrateCandidateCoreAsync(
                    current, bestCandidate, track.DurationSeconds, ct).ConfigureAwait(false);
                if (!ReferenceEquals(hydrated, bestCandidate))
                {
                    int topIndex = sortedPreviews.IndexOf(bestCandidate);
                    if (topIndex >= 0) sortedPreviews[topIndex] = hydrated;
                    bestCandidate = hydrated;
                }
            }
            // Inner catches already isolate provider failures; only propagate
            // cancellation so an aborted scan/dialog doesn't look like "no match".
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[TrackEnrichmentWorkflow] Top-candidate hydration failed: {ex.Message}");
            }
        }

        return new TrackEnrichmentPlan(track, topTier, sortedPreviews, bestCandidate);
    }

    /// <summary>
    /// ENR-02 companion: fills a non-top candidate's artwork and lyrics comparisons
    /// on demand (called when the user actually selects that candidate in the review
    /// dialog). Re-reads the local tag state so the comparison baselines match the
    /// ones the plan was built against.
    /// </summary>
    public async Task<CandidatePreview> HydrateCandidateAsync(
        Track track,
        CandidatePreview preview,
        CancellationToken ct = default)
    {
        if (track == null) throw new ArgumentNullException(nameof(track));
        if (preview == null) throw new ArgumentNullException(nameof(preview));

        LocalTagState state = await ReadLocalTagStateAsync(track).ConfigureAwait(false);
        var albumRecord = await _dbContext.GetAlbumByIdAsync(track.AlbumId).ConfigureAwait(false);
        state = state with { ArtworkUrl = albumRecord?.ArtworkUrl };

        return await HydrateCandidateCoreAsync(state, preview, track.DurationSeconds, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Snapshot of the local file's tag values that enrichment comparisons are
    /// built against. Shared by plan creation and candidate hydration so every
    /// consumer sees identical baselines.
    /// </summary>
    private sealed record LocalTagState(
        string Title,
        string ArtistName,
        string AlbumTitle,
        string? AlbumArtist,
        string? Composer,
        string? Genre,
        int? Year,
        int? TrackNumber,
        int? TrackCount,
        int? DiscNumber,
        int? DiscCount,
        string? Lyrics,
        string? ArtworkUrl,
        ExternalIds ExternalIds);

    private async Task<LocalTagState> ReadLocalTagStateAsync(Track track)
    {
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

        return new LocalTagState(
            currentTitle, currentArtist, currentAlbum, currentAlbumArtist, currentComposer,
            currentGenre, currentYear, currentTrackNumber, currentTrackCount, currentDiscNumber,
            currentDiscCount, currentLyrics, currentArtworkUrl, currentExtIds);
    }

    private async Task<CandidatePreview> HydrateCandidateCoreAsync(
        LocalTagState state,
        CandidatePreview preview,
        double fallbackDurationSeconds,
        CancellationToken ct)
    {
        // Fetch Candidate Artwork if available
        string? proposedArtworkUrl = null;
        byte[]? proposedArtworkBytes = null;
        string? proposedArtworkMime = null;

        if (_artworkProvider != null && _httpService != null)
        {
            try
            {
                var artUrls = await _artworkProvider.SearchAlbumArtworkUrlsAsync(
                    preview.Album.ProposedValue ?? preview.Album.CurrentValue ?? "",
                    preview.Artist.ProposedValue ?? preview.Artist.CurrentValue ?? "",
                    preview.ExternalIds.ProposedValue,
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[TrackEnrichmentWorkflow] Artwork retrieval failed for candidate '{preview.ProviderName}': {ex.Message}");
            }
        }

        // Fetch Candidate Lyrics if available
        string? proposedLyrics = null;
        if (_lyricsOrchestrator != null)
        {
            try
            {
                var lyricsResult = await _lyricsOrchestrator.FetchLyricsAsync(
                    preview.Title.ProposedValue ?? preview.Title.CurrentValue ?? "",
                    preview.Artist.ProposedValue ?? preview.Artist.CurrentValue ?? "",
                    preview.Album.ProposedValue ?? preview.Album.CurrentValue ?? "",
                    fallbackDurationSeconds > 0 ? fallbackDurationSeconds : null,
                    preview.ExternalIds.ProposedValue,
                    ct).ConfigureAwait(false);

                if (lyricsResult != null && lyricsResult.State != LyricsState.Unavailable)
                {
                    proposedLyrics = lyricsResult.PlainText ?? (lyricsResult.SyncedLines != null ? string.Join(Environment.NewLine, lyricsResult.SyncedLines.Select(l => $"[{l.Start:mm\\:ss\\.ff}] {l.Text}")) : null);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[TrackEnrichmentWorkflow] Lyrics retrieval failed for candidate '{preview.ProviderName}': {ex.Message}");
            }
        }

        return preview with
        {
            Lyrics = CreateComparison("Lyrics", state.Lyrics, proposedLyrics),
            ArtworkUrl = CreateComparison("Artwork", state.ArtworkUrl, proposedArtworkUrl),
            ProposedArtworkBytes = proposedArtworkBytes,
            ProposedArtworkMime = proposedArtworkMime
        };
    }

    private static FieldComparison<string> CreateComparison(string fieldName, string? current, string? proposed)
    {
        string? c = !string.IsNullOrWhiteSpace(current) ? current.Trim() : null;
        string? p = !string.IsNullOrWhiteSpace(proposed) ? proposed.Trim() : null;
        // ENR-04: this diff used to be case-INSENSITIVE, so a provider's
        // capitalization-only correction ("the beatles" -> "The Beatles") was never
        // surfaced as a change and silently dropped. Ordinal compare offers case
        // fixes while trimmed-equal strings still compare clean.
        bool diff = !string.Equals(c, p, StringComparison.Ordinal) && p != null;

        return new FieldComparison<string>(fieldName, c, p, diff, diff);
    }

    private static FieldComparison<int?> CreateNullableComparison(string fieldName, int? current, int? proposed)
    {
        bool diff = proposed.HasValue && proposed.Value > 0 && proposed != current;
        return new FieldComparison<int?>(fieldName, current, proposed, diff, diff);
    }
}
