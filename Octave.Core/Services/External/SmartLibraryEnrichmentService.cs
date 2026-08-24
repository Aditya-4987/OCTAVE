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

    // SLE-09: the apply loop only counted Title/Artist/Album as "metadata updated",
    // so a scan that fixed genre/year/track#/disc#/IDs reported metadataUpdated=0.
    // Every tag-text write counts now (external IDs included — they live in the tag).
    private static readonly EnrichmentActions MetadataFieldActions =
        EnrichmentActions.WriteTitle | EnrichmentActions.WriteArtist | EnrichmentActions.WriteAlbum |
        EnrichmentActions.WriteGenre | EnrichmentActions.WriteYear | EnrichmentActions.WriteTrackNumber |
        EnrichmentActions.WriteDiscNumber | EnrichmentActions.WriteExternalIds;

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

    // Deduplication caches across a scan session.
    // NF-16: plain Task<T> values made GetOrAdd's known factory race observable —
    // concurrent callers on the same key each ran their own factory (only one Task
    // won the slot, but every loser still executed its HTTP fetch). Lazy<T> with
    // ExecutionAndPublication guarantees exactly one factory invocation per key.
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _albumArtworkTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<EnrichedArtistProfile?>>> _artistEnrichmentTasks = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _scanCts;

    // SLE-06: serializes scans. Previously two overlapping scans raced through
    // _scanCts cancellation and cleared each other's dedup caches mid-flight.
    private readonly SemaphoreSlim _scanGate = new(1, 1);

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
        // SLE-06: overlapping scans used to cancel each other mid-flight AND clear
        // each other's dedup caches (duplicate provider calls racing on the same
        // artists/albums). Reject a second scan outright; CancelCurrentScan remains
        // the supported way to stop the running one.
        if (!await _scanGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            throw new InvalidOperationException("An enrichment scan is already running.");
        }

        try
        {
            return await RunEnrichmentScanCoreAsync(dryRun, ct).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<LibraryEnrichmentSummary> RunEnrichmentScanCoreAsync(bool dryRun, CancellationToken ct)
    {
        // SLE-06: dispose the superseded CTS — cancelling alone leaked its callback
        // registrations until finalization.
        _scanCts?.Cancel();
        _scanCts?.Dispose();
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

        // SLE-05: `allTracks.Select(async …)` + Task.WhenAll materialized one closure
        // and one state machine per track up front (for a 50k-track library that is
        // 50k queued continuations before any of them run). Parallel.ForEachAsync is
        // the idiomatic bounded form: same MaxDegreeOfParallelism cap, lazy iteration.
        try
        {
            await Parallel.ForEachAsync(allTracks,
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = token },
                async (track, itemCt) =>
            {
                if (itemCt.IsCancellationRequested) return;

                try
                {
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

                    // Build Execution Plan for track (dryRun suppresses Artist-record
                    // persistence — a preview scan must not mutate library data)
                    var plan = await BuildPlanForTrackAsync(track, settings, itemCt, dryRun).ConfigureAwait(false);

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
                catch (OperationCanceledException) when (itemCt.IsCancellationRequested || token.IsCancellationRequested)
                {
                    // Scan cancelled — skip without counting as failure.
                }
                catch (Exception ex)
                {
                    // SLE-07: a crashing track used to only bump the in-memory counter —
                    // nothing was persisted, so the review UI / session history showed
                    // the track's stale prior status forever. Persist a Failed plan.
                    Interlocked.Increment(ref failedCount);
                    System.Diagnostics.Debug.WriteLine($"[SmartLibraryEnrichmentService] Error processing track '{track.Title}': {ex.Message}");

                    var failedPlan = new TrackEnrichmentExecutionPlan
                    {
                        TrackId = track.Id,
                        TrackUri = track.SourceUri,
                        LocalTitle = track.Title,
                        LocalArtist = track.ArtistName,
                        LocalAlbum = track.AlbumTitle,
                        LocalDurationSeconds = track.DurationSeconds,
                        LocalYear = track.Year,
                        LocalTrackNumber = track.TrackNumber,
                        Status = EnrichmentTrackStatus.Failed,
                        ErrorMessage = ex.Message
                    };
                    lock (executionPlans)
                    {
                        executionPlans.Add(failedPlan);
                    }

                    try
                    {
                        await _dbContext.SetEnrichmentStateRecordAsync(
                            track.Id,
                            sessionId,
                            (int)EnrichmentTrackStatus.Failed,
                            0.0,
                            false,
                            JsonSerializer.Serialize(failedPlan, JsonOpts),
                            null).ConfigureAwait(false);
                    }
                    catch (Exception persistEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SmartLibraryEnrichmentService] Could not persist failure state for '{track.Title}': {persistEx.Message}");
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Scan cancelled between iterations — fall through to summary.
        }

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
                    // SLE-09: genre/year/track#/disc#/external-ID writes count as
                    // metadata updates too (only Title/Artist/Album did before).
                    if ((plan.PlannedActions & MetadataFieldActions) != 0)
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
        CancellationToken ct = default,
        bool dryRun = false)
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

        // SLE-01: capture the file's real identifiers while the tag is open — the
        // exact-ID safety gate below used to compare the path-hash Track.Id against a
        // candidate MBID, which can never match (dead gate).
        ExternalIds localIds = tagFile != null ? ExternalTagIds.Read(tagFile.Tag) : ExternalIds.Empty;

        // Track has no DiscNumber column; read it from the tag so the disc write below
        // can tell "missing" from "already set" like every other field.
        int localDiscNumber = tagFile != null && tagFile.Tag.Disc > 0 ? (int)tagFile.Tag.Disc : 0;

        var albumRecord = await _dbContext.GetAlbumByIdAsync(track.AlbumId).ConfigureAwait(false);
        var artistRecord = await _dbContext.GetArtistByIdAsync(track.ArtistId).ConfigureAwait(false);

        bool lyricsExist = !string.IsNullOrWhiteSpace(tagFile?.Tag.Lyrics);
        var completeness = EvaluateCompleteness(track, tagFile, albumRecord, artistRecord, lyricsExist);

        tagFile?.Dispose();

        // Check if track needs enrichment based on active settings. (SLE-02: these
        // flags used to be computed and never read — the ScanOnlyMissing* toggles were
        // dead. needsArtwork is intentionally absent: the artwork block below already
        // encodes the safe intersection of its settings.)
        bool needsMetadata = settings.ScanOnlyMissingMetadata ? !completeness.IsMetadataComplete : true;
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
        var gates = EvaluateHardSafetyGates(track, topCandidate, localIds);
        plan.SafetyGates = gates;

        // 4. Generate Actions & Updates
        // NF-27: low-confidence / gate-failed matches land in the manual review
        // queue, where "Apply Candidate" is the user's explicit consent to write.
        // These plans used to return HERE - before any proposal existed - so every
        // Apply attempt died with "No proposed changes in plan." They now fall
        // through the same action-building code below; nothing auto-applies them
        // (the scan's apply pass filters on SafeReadyToApply), so building the
        // proposal is purely preparatory.
        bool manualReview = !gates.Passed || topCandidate.Confidence < HighConfidenceThreshold;
        plan.Status = manualReview ? EnrichmentTrackStatus.NeedsReview : EnrichmentTrackStatus.SafeReadyToApply;

        var plannedActions = EnrichmentActions.None;
        var meta = topCandidate.Metadata;

        // INT-02/SLE-02: honor the write-policy settings, which were previously read
        // by nothing — AutoFillMissingMetadata (default false) did nothing, the
        // WritePolicy dropdown was inert, and ReplaceExistingMetadata was ignored.
        //  - AutoFillMissingMetadata is the master switch for automatic tag-text writes.
        //  - needsMetadata (ScanOnlyMissingMetadata) decides whether complete tracks
        //    are considered at all.
        //  - NeverWriteAutomatically disables every automatic metadata write.
        //  - NF-27: a manually-reviewed candidate bypasses all three — the review
        //    card shows current vs proposed side by side and Apply is the consent,
        //    so every field the candidate can improve is proposed for that one click.
        // External-ids writing stays governed solely by WriteExternalIdsToTags: ids are
        // not user-visible metadata, and writing them is what enables future exact-ID
        // matching (MATCH-01).
        bool considerMetadata = manualReview || (
            settings.AutoFillMissingMetadata &&
            settings.WritePolicy != MetadataWritePolicy.NeverWriteAutomatically &&
            needsMetadata);
        // Replacing a real (non-generic) local value additionally requires the explicit
        // replace permission AND an overwrite-capable policy — or an explicit manual
        // apply, where the side-by-side review card IS that permission (NF-27).
        bool mayReplaceExisting = manualReview || (
            settings.ReplaceExistingMetadata &&
            settings.WritePolicy is MetadataWritePolicy.WriteOnlyHighConfidence
                or MetadataWritePolicy.AlwaysPreferOnline);

        string? newTitle = null;
        if (considerMetadata && ShouldWriteField(IsMissingOrGeneric(track.Title), mayReplaceExisting, track.Title, meta.Title) && !string.IsNullOrWhiteSpace(meta.Title))
        {
            newTitle = meta.Title;
            plannedActions |= EnrichmentActions.WriteTitle;
        }

        string? newArtist = null;
        if (considerMetadata && ShouldWriteField(IsMissingOrGeneric(track.ArtistName), mayReplaceExisting, track.ArtistName, meta.ArtistName) && !string.IsNullOrWhiteSpace(meta.ArtistName))
        {
            newArtist = meta.ArtistName;
            plannedActions |= EnrichmentActions.WriteArtist;
        }

        string? newAlbum = null;
        if (considerMetadata && ShouldWriteField(IsMissingOrGeneric(track.AlbumTitle), mayReplaceExisting, track.AlbumTitle, meta.AlbumTitle) && !string.IsNullOrWhiteSpace(meta.AlbumTitle))
        {
            newAlbum = meta.AlbumTitle;
            plannedActions |= EnrichmentActions.WriteAlbum;
        }

        string? newGenre = null;
        if (considerMetadata && ShouldWriteField(string.IsNullOrWhiteSpace(track.Genre), mayReplaceExisting, track.Genre, meta.Genre) && !string.IsNullOrWhiteSpace(meta.Genre))
        {
            newGenre = meta.Genre;
            plannedActions |= EnrichmentActions.WriteGenre;
        }

        int? newYear = null;
        // EDITOR-06: provider values must pass the same sanity clamps as user
        // edits — a nonsense year/track number from a provider never reaches a tag.
        if (considerMetadata && ShouldWriteField(track.Year <= 0, mayReplaceExisting, track.Year, meta.Year.GetValueOrDefault()) && meta.Year.HasValue && MetadataValueClamps.IsValidYear(meta.Year.Value))
        {
            newYear = meta.Year.Value;
            plannedActions |= EnrichmentActions.WriteYear;
        }

        int? newTrackNum = null;
        if (considerMetadata && ShouldWriteField(track.TrackNumber <= 0, mayReplaceExisting, track.TrackNumber, meta.TrackNumber.GetValueOrDefault()) && meta.TrackNumber.HasValue && MetadataValueClamps.IsValidTrackNumber(meta.TrackNumber.Value))
        {
            newTrackNum = meta.TrackNumber.Value;
            plannedActions |= EnrichmentActions.WriteTrackNumber;
        }

        int? newDiscNum = null;
        if (considerMetadata && ShouldWriteField(localDiscNumber <= 0, mayReplaceExisting, localDiscNumber, meta.DiscNumber.GetValueOrDefault()) && meta.DiscNumber.HasValue && MetadataValueClamps.IsValidTrackNumber(meta.DiscNumber.Value))
        {
            // Previously wrote whenever the candidate carried a disc number, with no
            // local-value check at all (Track has no DiscNumber column) — every scan
            // restamped existing disc numbers. Now compares against the tag value.
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
        // SLE-02 note: deliberately NOT needs-flag gated. `!IsArtworkComplete ||
        // ReplaceExistingArtwork` is the safe intersection of the artwork settings —
        // with ScanOnlyMissingArtwork=false ("scan all") a needs-flag would fetch art
        // for complete albums and overwrite artwork the user never permitted replacing.
        byte[]? artworkBytes = null;
        string? artworkMime = null;
        if ((!completeness.IsArtworkComplete || settings.ReplaceExistingArtwork) && settings.AutoDownloadMissingArtwork)
        {
            string? releaseGroupId = topCandidate.ExternalIds?.AdditionalIds != null && topCandidate.ExternalIds.AdditionalIds.TryGetValue("MusicBrainzReleaseGroupId", out var rgid) ? rgid : null;
            string albumKey = !string.IsNullOrWhiteSpace(releaseGroupId)
                ? releaseGroupId
                : $"{meta.ArtistName ?? track.ArtistName} - {meta.AlbumTitle ?? track.AlbumTitle}";

            Task<string?> artworkResolution = _albumArtworkTasks.GetOrAdd(albumKey, key =>
                new Lazy<Task<string?>>(() =>
                    _artworkOrchestrator.ResolveAndCacheAlbumArtworkAsync(
                        meta.AlbumTitle ?? track.AlbumTitle,
                        meta.ArtistName ?? track.ArtistName,
                        topCandidate.ExternalIds,
                        ct,
                        // NF-31: scans upgrade pre-fix low-res cached covers.
                        preferHighResolutionUpgrade: true),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;

            string? artToken;
            try
            {
                artToken = await artworkResolution.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // SLE-04: a faulted task used to stay cached — one transient network
                // blip on an album's first track poisoned every other track of that
                // album for the whole session (they all awaited the same fault).
                _albumArtworkTasks.TryRemove(albumKey, out _);
                System.Diagnostics.Debug.WriteLine($"[SmartLibraryEnrichmentService] Artwork resolution failed for '{albumKey}': {ex.Message}");
                artToken = null;
            }

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
        // INT-03: was `(!IsArtistComplete || EnableArtistEnrichment)` — the right
        // operand defaulted true, making the gate always-true so every scan re-enriched
        // every artist. Intended logic: only incomplete artists, both toggles required.
        // NF-27: skipped for manual-review plans — this block writes to the shared
        // Artist record, which must not mutate before the user approves anything.
        if (!completeness.IsArtistComplete && settings.EnableArtistEnrichment &&
            settings.AutoDownloadMissingArtistImages && !manualReview)
        {
            string? artistMbid = topCandidate.ExternalIds?.AdditionalIds != null && topCandidate.ExternalIds.AdditionalIds.TryGetValue("MusicBrainzArtistId", out var aid) ? aid : null;
            string artistKey = !string.IsNullOrWhiteSpace(artistMbid)
                ? artistMbid
                : meta.ArtistName ?? track.ArtistName;

            Task<EnrichedArtistProfile?> artistResolution = _artistEnrichmentTasks.GetOrAdd(artistKey, key =>
                new Lazy<Task<EnrichedArtistProfile?>>(() =>
                    _artistEnrichmentService.GetEnrichedArtistAsync(
                        meta.ArtistName ?? track.ArtistName,
                        artistMbid,
                        ct),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;

            EnrichedArtistProfile? artistProfile;
            try
            {
                // SLE-03: this used to be fire-and-forget (`_ = GetOrAdd(...)`) — the
                // profile landed in the enrichment service's own cache only, the Artist
                // record stayed empty, IsArtistComplete never flipped true, and every
                // subsequent scan re-fetched the same artists while the summary counted
                // them as enriched anyway. Await it and persist below.
                artistProfile = await artistResolution.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Same eviction rationale as the artwork cache above (SLE-04).
                _artistEnrichmentTasks.TryRemove(artistKey, out _);
                System.Diagnostics.Debug.WriteLine($"[SmartLibraryEnrichmentService] Artist enrichment failed for '{artistKey}': {ex.Message}");
                artistProfile = null;
            }

            if (artistProfile != null &&
                (!string.IsNullOrWhiteSpace(artistProfile.Biography) || !string.IsNullOrWhiteSpace(artistProfile.LocalImageToken)))
            {
                string? newBio = !string.IsNullOrWhiteSpace(artistProfile.Biography) ? artistProfile.Biography : null;
                string? newArtworkUrl = !string.IsNullOrWhiteSpace(artistProfile.LocalImageToken) ? artistProfile.LocalImageToken : null;

                bool persisted = true;
                if (!dryRun)
                {
                    try
                    {
                        var freshArtist = await _dbContext.GetArtistByIdAsync(track.ArtistId).ConfigureAwait(false);
                        if (freshArtist != null &&
                            ((!string.Equals(freshArtist.Bio, newBio, StringComparison.Ordinal) && newBio != null) ||
                             (!string.Equals(freshArtist.ArtworkUrl, newArtworkUrl, StringComparison.Ordinal) && newArtworkUrl != null)))
                        {
                            var updated = freshArtist with
                            {
                                Bio = newBio ?? freshArtist.Bio,
                                ArtworkUrl = newArtworkUrl ?? freshArtist.ArtworkUrl
                            };
                            await _dbContext.UpsertArtistAsync(updated).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        persisted = false;
                        System.Diagnostics.Debug.WriteLine($"[SmartLibraryEnrichmentService] Artist record persist failed for '{track.ArtistId}': {ex.Message}");
                    }
                }

                // Flag only what actually happened (or would happen, for a dry run's
                // preview) so artistsEnriched stops over-reporting (SLE-03).
                if (persisted)
                {
                    plannedActions |= EnrichmentActions.EnrichArtistBioAndPhoto;
                }
            }
        }

        // Lyrics Retrieval
        // SLE-02: honor ScanOnlyMissingLyrics (needsLyrics was computed but never read).
        string? newLyrics = null;
        if (needsLyrics && settings.EnableOnlineLyrics)
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

    private SafetyGateResult EvaluateHardSafetyGates(Track localTrack, TrackMatchCandidate candidate, ExternalIds localIds)
    {
        var positiveEvidence = new List<string>();
        var warnings = new List<string>();

        // Exact MBID / ISRC Match satisfies gates directly. (SLE-01: compares the
        // local FILE's stored identifiers — the previous localTrack.Id comparison was
        // a path hash vs MBID and could never fire.)
        if (candidate.ExternalIds != null)
        {
            if (IsExactIdMatch(localIds.Isrc, candidate.ExternalIds.Isrc))
            {
                positiveEvidence.Add("Exact ISRC match");
                return new SafetyGateResult(true, true, true, true, true, positiveEvidence, warnings);
            }

            if (IsExactIdMatch(localIds.MusicBrainzId, candidate.ExternalIds.MusicBrainzId))
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

    // INT-02: a missing/generic local value is filled under every active policy;
    // overwriting a real value additionally requires ReplaceExistingMetadata plus an
    // overwrite-capable WritePolicy (see mayReplaceExisting at the call sites).
    // Minimal-diff plans: even with replace permission, a candidate whose value
    // already matches what the file stores must not be flagged as a write.
    private static bool ShouldWriteField(bool localValueMissingOrGeneric, bool mayReplaceExisting, string? localValue, string? candidateValue) =>
        (localValueMissingOrGeneric || mayReplaceExisting) &&
        !string.Equals(localValue?.Trim(), candidateValue?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ShouldWriteField(bool localValueMissingOrGeneric, bool mayReplaceExisting, int localValue, int candidateValue) =>
        (localValueMissingOrGeneric || mayReplaceExisting) && localValue != candidateValue;

    private static bool IsExactIdMatch(string? localId, string? candidateId) =>
        !string.IsNullOrWhiteSpace(localId) &&
        !string.IsNullOrWhiteSpace(candidateId) &&
        localId.Trim().Equals(candidateId.Trim(), StringComparison.OrdinalIgnoreCase);

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
