using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Interfaces;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Database;
using Octave.Core.Services.External.Artist;
using Octave.Core.Services.External.Settings;
using Octave.Core.Services.Library;
using Octave.Core.Services.Metadata;
using Octave.Core.Services.Network;

namespace Octave.Core.Services.External;

public class SmartLibraryEnrichmentService : ISmartLibraryEnrichmentService
{
    private const double HighConfidenceThreshold = 0.85;
    private const double TitleSimilarityThreshold = 0.70;
    private const double ArtistSimilarityThreshold = 0.70;
    private const double DurationToleranceSeconds = 5.0;

    private readonly SqliteDbContext _dbContext;
    private readonly ITrackMetadataMatcher _matcher;
    private readonly ITrackMetadataEditor _metadataEditor;
    private readonly IExternalArtworkOrchestrator _artworkOrchestrator;
    private readonly IArtistEnrichmentService _artistEnrichmentService;
    private readonly IOnlineLyricsOrchestrator _lyricsOrchestrator;
    private readonly IExternalDataSettingsService _settingsService;
    private readonly ILibraryService _libraryService;
    private readonly IArtworkCacheManager _artworkCacheManager;
    private readonly IHttpService _httpService;

    // Deduplication caches across a scan session
    private readonly ConcurrentDictionary<string, Task<string?>> _albumArtworkTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<EnrichedArtistProfile?>> _artistEnrichmentTasks = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _scanCts;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public event EventHandler<LibraryEnrichmentProgress>? ProgressChanged;
    public event EventHandler<LibraryEnrichmentSummary>? ScanCompleted;

    public SmartLibraryEnrichmentService(
        SqliteDbContext dbContext,
        ITrackMetadataMatcher matcher,
        ITrackMetadataEditor metadataEditor,
        IExternalArtworkOrchestrator artworkOrchestrator,
        IArtistEnrichmentService artistEnrichmentService,
        IOnlineLyricsOrchestrator lyricsOrchestrator,
        IExternalDataSettingsService settingsService,
        ILibraryService libraryService,
        IArtworkCacheManager artworkCacheManager,
        IHttpService httpService)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _metadataEditor = metadataEditor ?? throw new ArgumentNullException(nameof(metadataEditor));
        _artworkOrchestrator = artworkOrchestrator ?? throw new ArgumentNullException(nameof(artworkOrchestrator));
        _artistEnrichmentService = artistEnrichmentService ?? throw new ArgumentNullException(nameof(artistEnrichmentService));
        _lyricsOrchestrator = lyricsOrchestrator ?? throw new ArgumentNullException(nameof(lyricsOrchestrator));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _artworkCacheManager = artworkCacheManager ?? throw new ArgumentNullException(nameof(artworkCacheManager));
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
    }

    public async Task<LibraryEnrichmentSummary> RunEnrichmentScanAsync(bool dryRun, CancellationToken ct = default)
    {
        _scanCts?.Cancel();
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _scanCts.Token;

        var startTime = DateTimeOffset.UtcNow;
        string sessionId = Guid.NewGuid().ToString("N");

        _albumArtworkTasks.Clear();
        _artistEnrichmentTasks.Clear();

        // 1. Fetch Tracks from SQLite
        var allTracks = await _dbContext.GetAllTracksAsync().ConfigureAwait(false);
        int totalTracks = allTracks.Count;

        await _dbContext.SaveEnrichmentSessionAsync(
            sessionId,
            startTime.ToUnixTimeSeconds(),
            null,
            dryRun,
            totalTracks).ConfigureAwait(false);

        var executionPlans = new List<TrackEnrichmentExecutionPlan>();
        int scannedCount = 0;
        int safeReadyCount = 0;
        int enrichedCount = 0;
        int alreadyCompleteCount = 0;
        int needsReviewCount = 0;
        int noMatchCount = 0;
        int failedCount = 0;
        int metadataUpdated = 0;
        int artworkAdded = 0;
        int artistsEnriched = 0;
        int lyricsAdded = 0;

        var settings = _settingsService.CurrentSettings;
        int maxConcurrency = Math.Clamp(settings.MaxConcurrentRequests, 1, 8);
        using var throttle = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = allTracks.Select(async track =>
        {
            await throttle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (token.IsCancellationRequested) return;

                // Check persistent state for prior exclusion
                var existingState = await _dbContext.GetEnrichmentStateRecordAsync(track.Id).ConfigureAwait(false);
                if (existingState.HasValue && existingState.Value.Status == (int)EnrichmentTrackStatus.NeverAskAgain)
                {
                    Interlocked.Increment(ref scannedCount);
                    return;
                }

                // Notify live progress
                NotifyProgress(sessionId, totalTracks, scannedCount, safeReadyCount, enrichedCount,
                    alreadyCompleteCount, needsReviewCount, noMatchCount, failedCount,
                    track.Title, "Analyzing track...", isRunning: true, isCancelled: false);

                // Build Execution Plan for track
                var plan = await BuildPlanForTrackAsync(track, settings, token).ConfigureAwait(false);

                lock (executionPlans)
                {
                    executionPlans.Add(plan);
                }

                Interlocked.Increment(ref scannedCount);

                switch (plan.Status)
                {
                    case EnrichmentTrackStatus.AlreadyComplete:
                        Interlocked.Increment(ref alreadyCompleteCount);
                        break;
                    case EnrichmentTrackStatus.SafeReadyToApply:
                        Interlocked.Increment(ref safeReadyCount);
                        break;
                    case EnrichmentTrackStatus.NeedsReview:
                        Interlocked.Increment(ref needsReviewCount);
                        break;
                    case EnrichmentTrackStatus.NoMatch:
                        Interlocked.Increment(ref noMatchCount);
                        break;
                    case EnrichmentTrackStatus.Failed:
                        Interlocked.Increment(ref failedCount);
                        break;
                }

                // Persist state in SQLite
                string? planJson = JsonSerializer.Serialize(plan, JsonOpts);
                string? candsJson = plan.AlternativeCandidates.Count > 0 ? JsonSerializer.Serialize(plan.AlternativeCandidates, JsonOpts) : null;

                await _dbContext.SetEnrichmentStateRecordAsync(
                    track.Id,
                    sessionId,
                    (int)plan.Status,
                    plan.Confidence,
                    plan.SafetyGates.Passed,
                    planJson,
                    candsJson).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Scan cancelled
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failedCount);
                System.Diagnostics.Debug.WriteLine($"[SmartLibraryEnrichmentService] Error processing track '{track.Title}': {ex.Message}");
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // 2. If NOT Dry-Run: Automatically Apply Safe Plans
        if (!dryRun && !token.IsCancellationRequested)
        {
            var safePlans = executionPlans.Where(p => p.Status == EnrichmentTrackStatus.SafeReadyToApply).ToList();
            foreach (var plan in safePlans)
            {
                if (token.IsCancellationRequested) break;

                NotifyProgress(sessionId, totalTracks, scannedCount, safeReadyCount, enrichedCount,
                    alreadyCompleteCount, needsReviewCount, noMatchCount, failedCount,
                    plan.LocalTitle, "Applying safe metadata changes...", isRunning: true, isCancelled: false);

                var applyResult = await ApplySinglePlanInternalAsync(plan, token).ConfigureAwait(false);
                if (applyResult.Success)
                {
                    enrichedCount++;
                    if (plan.PlannedActions.HasFlag(EnrichmentActions.WriteTitle) ||
                        plan.PlannedActions.HasFlag(EnrichmentActions.WriteArtist) ||
                        plan.PlannedActions.HasFlag(EnrichmentActions.WriteAlbum))
                    {
                        metadataUpdated++;
                    }
                    if (plan.PlannedActions.HasFlag(EnrichmentActions.WriteArtwork)) artworkAdded++;
                    if (plan.PlannedActions.HasFlag(EnrichmentActions.EnrichArtistBioAndPhoto)) artistsEnriched++;
                    if (plan.PlannedActions.HasFlag(EnrichmentActions.WriteLyrics)) lyricsAdded++;
                }
            }

            _libraryService.NotifyLibraryUpdated();
        }

        var endTime = DateTimeOffset.UtcNow;
        await _dbContext.SaveEnrichmentSessionAsync(
            sessionId,
            startTime.ToUnixTimeSeconds(),
            endTime.ToUnixTimeSeconds(),
            dryRun,
            totalTracks).ConfigureAwait(false);

        var summary = new LibraryEnrichmentSummary(
            sessionId,
            dryRun,
            totalTracks,
            scannedCount,
            safeReadyCount,
            enrichedCount,
            alreadyCompleteCount,
            needsReviewCount,
            noMatchCount,
            failedCount,
            metadataUpdated,
            artworkAdded,
            artistsEnriched,
            lyricsAdded,
            endTime - startTime,
            executionPlans.AsReadOnly());

        NotifyProgress(sessionId, totalTracks, scannedCount, safeReadyCount, enrichedCount,
            alreadyCompleteCount, needsReviewCount, noMatchCount, failedCount,
            "Complete", "Scan completed.", 100.0, isRunning: false, isCancelled: token.IsCancellationRequested);

        ScanCompleted?.Invoke(this, summary);
        return summary;
    }

    public async Task<TrackEnrichmentExecutionPlan> BuildPlanForTrackAsync(
        Track track,
        ExternalDataSettings settings,
        CancellationToken ct = default)
    {
        var plan = new TrackEnrichmentExecutionPlan
        {
            TrackId = track.Id,
            TrackUri = track.SourceUri,
            LocalTitle = track.Title,
            LocalArtist = track.ArtistName,
            LocalAlbum = track.AlbumTitle,
            LocalDurationSeconds = track.DurationSeconds,
            LocalYear = track.Year,
            LocalTrackNumber = track.TrackNumber
        };

        if (string.IsNullOrWhiteSpace(track.SourceUri) || !File.Exists(track.SourceUri))
        {
            plan.Status = EnrichmentTrackStatus.Failed;
            plan.ErrorMessage = "Audio file not found on disk.";
            return plan;
        }

        // 1. Evaluate Multi-Dimensional Completeness
        TagLib.File? tagFile = null;
        try { tagFile = TagLib.File.Create(track.SourceUri); } catch { }

        var albumRecord = await _dbContext.GetAlbumByIdAsync(track.AlbumId).ConfigureAwait(false);
        var artistRecord = await _dbContext.GetArtistByIdAsync(track.ArtistId).ConfigureAwait(false);

        bool lyricsExist = !string.IsNullOrWhiteSpace(tagFile?.Tag.Lyrics);
        var completeness = EvaluateCompleteness(track, tagFile, albumRecord, artistRecord, lyricsExist);

        tagFile?.Dispose();

        // Check if track needs enrichment based on active settings
        bool needsMetadata = settings.ScanOnlyMissingMetadata ? !completeness.IsMetadataComplete : true;
        bool needsArtwork = settings.ScanOnlyMissingArtwork ? !completeness.IsArtworkComplete : true;
        bool needsLyrics = settings.ScanOnlyMissingLyrics ? !completeness.IsLyricsComplete : true;

        if (completeness.IsFullyComplete && !settings.ScanEntireLibrary)
        {
            plan.Status = EnrichmentTrackStatus.AlreadyComplete;
            return plan;
        }

        // 2. Query External Candidates via Matcher
        var candidates = await _matcher.FindMatchesForTrackAsync(track, ct).ConfigureAwait(false);
        if (candidates == null || candidates.Count == 0)
        {
            plan.Status = EnrichmentTrackStatus.NoMatch;
            return plan;
        }

        plan.AlternativeCandidates = candidates;
        var topCandidate = candidates[0];
        plan.SelectedCandidate = topCandidate;
        plan.Confidence = topCandidate.Confidence;

        // 3. Evaluate Mandatory Hard Safety Gates
        var gates = EvaluateHardSafetyGates(track, topCandidate);
        plan.SafetyGates = gates;

        // 4. Generate Actions & Updates
        if (!gates.Passed || topCandidate.Confidence < HighConfidenceThreshold)
        {
            plan.Status = EnrichmentTrackStatus.NeedsReview;
            return plan;
        }

        // Gates passed & score is high -> safe to plan automatic enrichment
        plan.Status = EnrichmentTrackStatus.SafeReadyToApply;

        var plannedActions = EnrichmentActions.None;
        var meta = topCandidate.Metadata;

        string? newTitle = null;
        if (IsMissingOrGeneric(track.Title) && !string.IsNullOrWhiteSpace(meta.Title))
        {
            newTitle = meta.Title;
            plannedActions |= EnrichmentActions.WriteTitle;
        }

        string? newArtist = null;
        if (IsMissingOrGeneric(track.ArtistName) && !string.IsNullOrWhiteSpace(meta.ArtistName))
        {
            newArtist = meta.ArtistName;
            plannedActions |= EnrichmentActions.WriteArtist;
        }

        string? newAlbum = null;
        if (IsMissingOrGeneric(track.AlbumTitle) && !string.IsNullOrWhiteSpace(meta.AlbumTitle))
        {
            newAlbum = meta.AlbumTitle;
            plannedActions |= EnrichmentActions.WriteAlbum;
        }

        string? newGenre = null;
        if (string.IsNullOrWhiteSpace(track.Genre) && !string.IsNullOrWhiteSpace(meta.Genre))
        {
            newGenre = meta.Genre;
            plannedActions |= EnrichmentActions.WriteGenre;
        }

        int? newYear = null;
        if (track.Year <= 0 && meta.Year.HasValue && meta.Year.Value > 0)
        {
            newYear = meta.Year.Value;
            plannedActions |= EnrichmentActions.WriteYear;
        }

        int? newTrackNum = null;
        if (track.TrackNumber <= 0 && meta.TrackNumber.HasValue && meta.TrackNumber.Value > 0)
        {
            newTrackNum = meta.TrackNumber.Value;
            plannedActions |= EnrichmentActions.WriteTrackNumber;
        }

        int? newDiscNum = null;
        if (meta.DiscNumber.HasValue && meta.DiscNumber.Value > 0)
        {
            newDiscNum = meta.DiscNumber.Value;
            plannedActions |= EnrichmentActions.WriteDiscNumber;
        }

        ExternalIds? newExtIds = null;
        if (settings.WriteExternalIdsToTags && topCandidate.ExternalIds != null)
        {
            newExtIds = topCandidate.ExternalIds;
            plannedActions |= EnrichmentActions.WriteExternalIds;
        }

        // Artwork Deduplication & Fetch
        byte[]? artworkBytes = null;
        string? artworkMime = null;
        if ((!completeness.IsArtworkComplete || settings.ReplaceExistingArtwork) && settings.AutoDownloadMissingArtwork)
        {
            string? releaseGroupId = topCandidate.ExternalIds?.AdditionalIds != null && topCandidate.ExternalIds.AdditionalIds.TryGetValue("MusicBrainzReleaseGroupId", out var rgid) ? rgid : null;
            string albumKey = !string.IsNullOrWhiteSpace(releaseGroupId)
                ? releaseGroupId
                : $"{meta.ArtistName ?? track.ArtistName} - {meta.AlbumTitle ?? track.AlbumTitle}";

            string? artToken = await _albumArtworkTasks.GetOrAdd(albumKey, async key =>
            {
                return await _artworkOrchestrator.ResolveAndCacheAlbumArtworkAsync(
                    meta.AlbumTitle ?? track.AlbumTitle,
                    meta.ArtistName ?? track.ArtistName,
                    topCandidate.ExternalIds,
                    ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(artToken))
            {
                string cachedFile = Path.Combine(_artworkCacheManager.CacheRoot, artToken);
                if (File.Exists(cachedFile))
                {
                    artworkBytes = await File.ReadAllBytesAsync(cachedFile, ct).ConfigureAwait(false);
                    artworkMime = "image/jpeg";
                    plannedActions |= EnrichmentActions.WriteArtwork;
                }
            }
        }

        // Artist Bio & Photo Deduplication
        if ((!completeness.IsArtistComplete || settings.EnableArtistEnrichment) && settings.AutoDownloadMissingArtistImages)
        {
            string? artistMbid = topCandidate.ExternalIds?.AdditionalIds != null && topCandidate.ExternalIds.AdditionalIds.TryGetValue("MusicBrainzArtistId", out var aid) ? aid : null;
            string artistKey = !string.IsNullOrWhiteSpace(artistMbid)
                ? artistMbid
                : meta.ArtistName ?? track.ArtistName;

            _ = _artistEnrichmentTasks.GetOrAdd(artistKey, async key =>
            {
                return await _artistEnrichmentService.GetEnrichedArtistAsync(
                    meta.ArtistName ?? track.ArtistName,
                    artistMbid,
                    ct).ConfigureAwait(false);
            });
            plannedActions |= EnrichmentActions.EnrichArtistBioAndPhoto;
        }

        // Lyrics Retrieval
        string? newLyrics = null;
        if (!completeness.IsLyricsComplete && settings.EnableOnlineLyrics)
        {
            try
            {
                var lyricsResult = await _lyricsOrchestrator.FetchLyricsAsync(
                    meta.Title ?? track.Title,
                    meta.ArtistName ?? track.ArtistName,
                    meta.AlbumTitle ?? track.AlbumTitle,
                    track.DurationSeconds > 0 ? track.DurationSeconds : meta.DurationSeconds,
                    topCandidate.ExternalIds,
                    ct).ConfigureAwait(false);

                if (lyricsResult != null && lyricsResult.State != LyricsState.Unavailable)
                {
                    string? lyricsText = lyricsResult.PlainText ?? (lyricsResult.SyncedLines != null && lyricsResult.SyncedLines.Count > 0 ? string.Join(Environment.NewLine, lyricsResult.SyncedLines.Select(l => l.Text)) : null);
                    if (!string.IsNullOrWhiteSpace(lyricsText))
                    {
                        newLyrics = lyricsText;
                        plannedActions |= EnrichmentActions.WriteLyrics;
                    }
                }
            }
            catch { }
        }

        plan.PlannedActions = plannedActions;
        plan.ProposedUpdate = new TrackMetadataUpdate(
            Title: newTitle,
            ArtistName: newArtist,
            AlbumTitle: newAlbum,
            AlbumArtist: null,
            Composer: null,
            Genre: newGenre,
            Year: newYear,
            TrackNumber: newTrackNum,
            TrackCount: null,
            DiscNumber: newDiscNum,
            DiscCount: null,
            Lyrics: newLyrics,
            NewArtworkBytes: artworkBytes,
            ArtworkMimeType: artworkMime,
            ClearArtwork: false,
            ExternalIds: newExtIds);

        return plan;
    }

    public async Task<IReadOnlyList<TrackEnrichmentExecutionPlan>> GetReviewQueueAsync(CancellationToken ct = default)
    {
        var records = await _dbContext.GetEnrichmentRecordsByStatusAsync((int)EnrichmentTrackStatus.NeedsReview).ConfigureAwait(false);
        var plans = new List<TrackEnrichmentExecutionPlan>();

        foreach (var r in records)
        {
            if (!string.IsNullOrWhiteSpace(r.PlanJson))
            {
                try
                {
                    var plan = JsonSerializer.Deserialize<TrackEnrichmentExecutionPlan>(r.PlanJson, JsonOpts);
                    if (plan != null) plans.Add(plan);
                }
                catch { }
            }
        }

        return plans;
    }

    public async Task<EnrichmentApplyResult> ApplySinglePlanAsync(TrackEnrichmentExecutionPlan plan, CancellationToken ct = default)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));

        var result = await ApplySinglePlanInternalAsync(plan, ct).ConfigureAwait(false);
        _libraryService.NotifyLibraryUpdated();
        return result;
    }

    public async Task<int> ApplySafePlansAsync(IEnumerable<TrackEnrichmentExecutionPlan> plans, CancellationToken ct = default)
    {
        if (plans == null) return 0;

        int successCount = 0;
        foreach (var plan in plans)
        {
            if (ct.IsCancellationRequested) break;
            var res = await ApplySinglePlanInternalAsync(plan, ct).ConfigureAwait(false);
            if (res.Success) successCount++;
        }

        _libraryService.NotifyLibraryUpdated();
        return successCount;
    }

    public async Task RejectOrSkipTrackAsync(string trackId, bool neverAskAgain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return;

        int status = neverAskAgain ? (int)EnrichmentTrackStatus.NeverAskAgain : (int)EnrichmentTrackStatus.Skipped;
        await _dbContext.SetEnrichmentStateRecordAsync(
            trackId,
            "manual_decision",
            status,
            0.0,
            false,
            null,
            null).ConfigureAwait(false);
    }

    public void CancelScan()
    {
        _scanCts?.Cancel();
    }

    public async Task ResetSessionStateAsync(CancellationToken ct = default)
    {
        await _dbContext.ClearEnrichmentStateAsync().ConfigureAwait(false);
    }

    private async Task<EnrichmentApplyResult> ApplySinglePlanInternalAsync(TrackEnrichmentExecutionPlan plan, CancellationToken ct)
    {
        if (plan.ProposedUpdate == null)
        {
            var dummyFileRes = FileWriteResult.Failed(plan.TrackUri, "No proposed changes in plan.");
            var dummyEdit = MetadataEditResult.FileFailed(dummyFileRes);
            return new EnrichmentApplyResult(false, dummyEdit, Array.Empty<string>(), "No proposed changes in plan.");
        }

        var editResult = await _metadataEditor.UpdateTrackMetadataAsync(plan.TrackId, plan.ProposedUpdate, ct).ConfigureAwait(false);

        int status = editResult.Success ? (int)EnrichmentTrackStatus.EnrichedSuccessfully : (int)EnrichmentTrackStatus.Failed;
        await _dbContext.SetEnrichmentStateRecordAsync(
            plan.TrackId,
            "applied",
            status,
            plan.Confidence,
            plan.SafetyGates.Passed,
            JsonSerializer.Serialize(plan, JsonOpts),
            null).ConfigureAwait(false);

        var appliedFields = new List<string>();
        if (plan.PlannedActions.HasFlag(EnrichmentActions.WriteTitle)) appliedFields.Add("Title");
        if (plan.PlannedActions.HasFlag(EnrichmentActions.WriteArtist)) appliedFields.Add("Artist");
        if (plan.PlannedActions.HasFlag(EnrichmentActions.WriteAlbum)) appliedFields.Add("Album");
        if (plan.PlannedActions.HasFlag(EnrichmentActions.WriteGenre)) appliedFields.Add("Genre");
        if (plan.PlannedActions.HasFlag(EnrichmentActions.WriteYear)) appliedFields.Add("Year");
        if (plan.PlannedActions.HasFlag(EnrichmentActions.WriteTrackNumber)) appliedFields.Add("TrackNumber");
        if (plan.PlannedActions.HasFlag(EnrichmentActions.WriteArtwork)) appliedFields.Add("Artwork");
        if (plan.PlannedActions.HasFlag(EnrichmentActions.WriteLyrics)) appliedFields.Add("Lyrics");
        if (plan.PlannedActions.HasFlag(EnrichmentActions.WriteExternalIds)) appliedFields.Add("ExternalIds");

        return new EnrichmentApplyResult(
            editResult.Success,
            editResult,
            appliedFields.AsReadOnly(),
            editResult.SummaryMessage);
    }

    private SafetyGateResult EvaluateHardSafetyGates(Track localTrack, TrackMatchCandidate candidate)
    {
        var positiveEvidence = new List<string>();
        var warnings = new List<string>();

        // Exact MBID / ISRC Match satisfies gates directly
        if (candidate.ExternalIds != null)
        {
            if (!string.IsNullOrWhiteSpace(candidate.ExternalIds.MusicBrainzId) &&
                localTrack.Id.Equals(candidate.ExternalIds.MusicBrainzId, StringComparison.OrdinalIgnoreCase))
            {
                positiveEvidence.Add("Exact MusicBrainz ID match");
                return new SafetyGateResult(true, true, true, true, true, positiveEvidence, warnings);
            }
        }

        // Gate 1: Version Incompatibility Check (MANDATORY GATE)
        var localVersion = MetadataTextNormalizer.ExtractVersionInfo(localTrack.Title, localTrack.AlbumTitle);
        var candVersion = MetadataTextNormalizer.ExtractVersionInfo(candidate.Metadata.Title, candidate.Metadata.AlbumTitle);

        bool versionMismatch = false;
        if (localVersion.IsLive != candVersion.IsLive)
        {
            versionMismatch = true;
            warnings.Add(localVersion.IsLive ? "Version Mismatch: Local is Live, Candidate is Studio" : "Version Mismatch: Local is Studio, Candidate is Live");
        }
        if (localVersion.IsRemix != candVersion.IsRemix)
        {
            versionMismatch = true;
            warnings.Add("Version Mismatch: Remix vs Original");
        }
        if (localVersion.IsAcoustic != candVersion.IsAcoustic)
        {
            versionMismatch = true;
            warnings.Add("Version Mismatch: Acoustic vs Studio");
        }
        if (localVersion.IsInstrumental != candVersion.IsInstrumental)
        {
            versionMismatch = true;
            warnings.Add("Version Mismatch: Instrumental vs Vocal");
        }

        // Gate 2: Title Similarity Check
        double titleSim = MetadataTextNormalizer.CalculateSimilarity(localTrack.Title, candidate.Metadata.Title);
        bool titlePassed = titleSim >= TitleSimilarityThreshold;
        if (titlePassed)
            positiveEvidence.Add(titleSim >= 0.95 ? "Exact Title match" : $"Strong Title similarity ({(titleSim * 100):0}%)");
        else
            warnings.Add($"Low Title similarity ({(titleSim * 100):0}%)");

        // Gate 3: Artist Similarity Check
        double artistSim = MetadataTextNormalizer.CalculateSimilarity(localTrack.ArtistName, candidate.Metadata.ArtistName);
        bool artistPassed = artistSim >= ArtistSimilarityThreshold;
        if (artistPassed)
            positiveEvidence.Add(artistSim >= 0.95 ? "Exact Artist match" : $"Strong Artist similarity ({(artistSim * 100):0}%)");
        else
            warnings.Add($"Low Artist similarity ({(artistSim * 100):0}%)");

        // Gate 4: Duration Tolerance Check
        bool durationPassed = true;
        if (localTrack.DurationSeconds > 0 && candidate.Metadata.DurationSeconds.HasValue && candidate.Metadata.DurationSeconds.Value > 0)
        {
            double diff = Math.Abs(localTrack.DurationSeconds - candidate.Metadata.DurationSeconds.Value);
            durationPassed = diff <= DurationToleranceSeconds;
            if (durationPassed)
                positiveEvidence.Add($"Duration match within ±{diff:0.#}s");
            else
                warnings.Add($"Duration differs by {diff:0.#}s (Exceeds {DurationToleranceSeconds:0}s tolerance)");
        }

        // Gate 5: Overall Confidence Check
        bool scorePassed = candidate.Confidence >= HighConfidenceThreshold;
        if (!scorePassed)
        {
            warnings.Add($"Confidence score {(candidate.Confidence * 100):0}% is below safety threshold {(HighConfidenceThreshold * 100):0}%");
        }

        bool allPassed = !versionMismatch && titlePassed && artistPassed && durationPassed && scorePassed;
        return new SafetyGateResult(allPassed, titlePassed, artistPassed, durationPassed, !versionMismatch, positiveEvidence, warnings);
    }

    private static TrackCompleteness EvaluateCompleteness(
        Track track,
        TagLib.File? tagFile,
        Album? albumRecord,
        Octave.Core.Models.Artist? artistRecord,
        bool lyricsExist)
    {
        bool metaComplete = !IsMissingOrGeneric(track.Title) &&
                            !IsMissingOrGeneric(track.ArtistName) &&
                            !IsMissingOrGeneric(track.AlbumTitle) &&
                            track.Year > 0 &&
                            track.TrackNumber > 0;

        bool artComplete = !string.IsNullOrWhiteSpace(albumRecord?.ArtworkUrl) ||
                           (tagFile?.Tag.Pictures != null && tagFile.Tag.Pictures.Length > 0);

        bool artistComplete = artistRecord != null &&
                              (!string.IsNullOrWhiteSpace(artistRecord.Bio) || !string.IsNullOrWhiteSpace(artistRecord.ArtworkUrl));

        return new TrackCompleteness(metaComplete, artComplete, artistComplete, lyricsExist);
    }

    private static bool IsMissingOrGeneric(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        string t = text.Trim().ToLowerInvariant();
        return t == "unknown artist" || t == "unknown" || t == "track" || t.StartsWith("track ");
    }

    private void NotifyProgress(
        string sessionId,
        int total,
        int scanned,
        int safe,
        int enriched,
        int complete,
        int review,
        int noMatch,
        int failed,
        string trackTitle,
        string operation,
        double? customPercentage = null,
        bool isRunning = true,
        bool isCancelled = false)
    {
        double pct = customPercentage ?? (total > 0 ? Math.Round((double)scanned / total * 100.0, 1) : 0.0);
        var prog = new LibraryEnrichmentProgress(
            sessionId,
            total,
            scanned,
            safe,
            enriched,
            complete,
            review,
            noMatch,
            failed,
            trackTitle,
            operation,
            pct,
            isRunning,
            isCancelled);

        ProgressChanged?.Invoke(this, prog);
    }
}
