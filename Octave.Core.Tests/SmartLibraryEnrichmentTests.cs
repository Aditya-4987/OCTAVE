using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Octave.Core.Interfaces;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Cache;
using Octave.Core.Services.Database;
using Octave.Core.Services.External;
using Octave.Core.Services.External.Artist;
using Octave.Core.Services.External.Artwork;
using Octave.Core.Services.External.Lyrics;
using Octave.Core.Services.External.MusicBrainz;
using Octave.Core.Services.External.Settings;
using Octave.Core.Services.Library;
using Octave.Core.Services.Metadata;
using Octave.Core.Services.Network;
using Xunit;

namespace Octave.Core.Tests;

public class SmartLibraryEnrichmentTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDbContext _dbContext;
    private readonly string _cachePath;
    private readonly ArtworkCacheManager _artworkCacheManager;

    public SmartLibraryEnrichmentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_SmartEnrichTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _dbContext = new SqliteDbContext($"Data Source={_dbPath}");
        _dbContext.InitializeAsync().GetAwaiter().GetResult();

        _cachePath = Path.Combine(_tempDir, "art_cache");
        Directory.CreateDirectory(_cachePath);
        _artworkCacheManager = new ArtworkCacheManager(_cachePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> HandlerFunc { get; set; } =
            (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            HandlerFunc(request, cancellationToken);
    }

    private string CreateTestMp3(string fileName, string? title = null, string? artist = null, string? album = null, int year = 0, int trackNum = 0)
    {
        string path = Path.Combine(_tempDir, fileName);
        byte[] dummyMp3 = new byte[] {
            0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };
        File.WriteAllBytes(path, dummyMp3);

        using (var tagFile = TagLib.File.Create(path))
        {
            if (title != null) tagFile.Tag.Title = title;
            if (artist != null) tagFile.Tag.Performers = new[] { artist };
            if (album != null) tagFile.Tag.Album = album;
            if (year > 0) tagFile.Tag.Year = (uint)year;
            if (trackNum > 0) tagFile.Tag.Track = (uint)trackNum;
            tagFile.Save();
        }

        return path;
    }

    // =================================================================
    // 1. DRY-RUN MODE: Writes Zero Files
    // =================================================================

    [Fact]
    public async Task DryRun_GeneratesExecutionPlans_WithoutWritingToDisk()
    {
        string filePath = CreateTestMp3("dryrun_track.mp3", "Speed of Sound", "Coldplay", "Unknown Album");
        var track = new Track("t_dry", "Speed of Sound", "a1", "Coldplay", "alb1", "Unknown Album", 210.0, filePath, "Local", 0, 0, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        var mockMatcher = new Mock<ITrackMetadataMatcher>();
        var candidateMeta = new ExternalTrackMetadata("Speed of Sound", "Coldplay", "X&Y", 2005, "Rock", 3, 1, 210.0, "ISRC123", new ExternalIds(MusicBrainzId: "mb_rec_1"));
        var candidate = new TrackMatchCandidate("MusicBrainz", new ExternalIds(MusicBrainzId: "mb_rec_1"), 0.95, "Exact Match", candidateMeta);
        mockMatcher.Setup(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candidate });

        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var mockEditor = new Mock<ITrackMetadataEditor>();
        var mockArtworkOrch = new Mock<IExternalArtworkOrchestrator>();
        var mockArtistService = new Mock<IArtistEnrichmentService>();
        var mockLyricsOrch = new Mock<IOnlineLyricsOrchestrator>();
        var mockLibraryService = new Mock<ILibraryService>();

        var service = new SmartLibraryEnrichmentService(
            _dbContext,
            mockMatcher.Object,
            mockEditor.Object,
            mockArtworkOrch.Object,
            mockArtistService.Object,
            mockLyricsOrch.Object,
            settingsService,
            mockLibraryService.Object,
            _artworkCacheManager,
            httpService);

        var summary = await service.RunEnrichmentScanAsync(dryRun: true);

        Assert.True(summary.IsDryRun);
        Assert.Equal(1, summary.TotalTracks);
        Assert.Equal(1, summary.SafeReadyCount);
        Assert.Equal(0, summary.EnrichedCount); // Dry-run writes zero changes!
        mockEditor.Verify(e => e.UpdateTrackMetadataAsync(It.IsAny<string>(), It.IsAny<TrackMetadataUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =================================================================
    // 2. MANDATORY HARD SAFETY GATES: Version Incompatibility Rejection
    // =================================================================

    [Fact]
    public async Task MandatoryGates_RejectsHighScoringVersionMismatch_RoutesToReview()
    {
        // Local is Studio; Candidate is Live -> Score 0.92, but Gate must fail!
        string filePath = CreateTestMp3("live_studio_test.mp3", "Fix You", "Coldplay", "X&Y", 2005, 4);
        var track = new Track("t_version", "Fix You", "a1", "Coldplay", "alb1", "X&Y", 295.0, filePath, "Local", 4, 2005, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        var mockMatcher = new Mock<ITrackMetadataMatcher>();
        var candidateMeta = new ExternalTrackMetadata("Fix You (Live in Sydney)", "Coldplay", "Live 2012", 2012, "Rock", 10, 1, 300.0, null, new ExternalIds(MusicBrainzId: "mb_live_1"));
        var candidate = new TrackMatchCandidate("MusicBrainz", new ExternalIds(MusicBrainzId: "mb_live_1"), 0.92, "High score candidate", candidateMeta);
        mockMatcher.Setup(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candidate });

        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var mockEditor = new Mock<ITrackMetadataEditor>();
        var service = new SmartLibraryEnrichmentService(
            _dbContext,
            mockMatcher.Object,
            mockEditor.Object,
            new Mock<IExternalArtworkOrchestrator>().Object,
            new Mock<IArtistEnrichmentService>().Object,
            new Mock<IOnlineLyricsOrchestrator>().Object,
            settingsService,
            new Mock<ILibraryService>().Object,
            _artworkCacheManager,
            httpService);

        var summary = await service.RunEnrichmentScanAsync(dryRun: false);

        Assert.Equal(1, summary.NeedsReviewCount);
        Assert.Equal(0, summary.SafeReadyCount);
        Assert.Equal(0, summary.EnrichedCount);
        mockEditor.Verify(e => e.UpdateTrackMetadataAsync(It.IsAny<string>(), It.IsAny<TrackMetadataUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =================================================================
    // 3. MANDATORY HARD SAFETY GATES: Duration Mismatch Rejection
    // =================================================================

    [Fact]
    public async Task MandatoryGates_RejectsDurationMismatchExceedingTolerance()
    {
        // Local duration 180s, Candidate duration 240s (diff 60s > 5s tolerance)
        string filePath = CreateTestMp3("duration_test.mp3", "Yellow", "Coldplay", "Parachutes", 2000, 5);
        var track = new Track("t_dur", "Yellow", "a1", "Coldplay", "alb1", "Parachutes", 180.0, filePath, "Local", 5, 2000, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        var mockMatcher = new Mock<ITrackMetadataMatcher>();
        var candidateMeta = new ExternalTrackMetadata("Yellow", "Coldplay", "Parachutes", 2000, "Rock", 5, 1, 240.0, null, new ExternalIds(MusicBrainzId: "mb_yel_1"));
        var candidate = new TrackMatchCandidate("MusicBrainz", new ExternalIds(MusicBrainzId: "mb_yel_1"), 0.90, "High match", candidateMeta);
        mockMatcher.Setup(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candidate });

        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var service = new SmartLibraryEnrichmentService(
            _dbContext,
            mockMatcher.Object,
            new Mock<ITrackMetadataEditor>().Object,
            new Mock<IExternalArtworkOrchestrator>().Object,
            new Mock<IArtistEnrichmentService>().Object,
            new Mock<IOnlineLyricsOrchestrator>().Object,
            settingsService,
            new Mock<ILibraryService>().Object,
            _artworkCacheManager,
            httpService);

        var summary = await service.RunEnrichmentScanAsync(dryRun: false);

        Assert.Equal(1, summary.NeedsReviewCount);
        Assert.Equal(0, summary.SafeReadyCount);
    }

    // =================================================================
    // 4. ENTITY DEDUPLICATION: Artist & Album Resolved Once
    // =================================================================

    [Fact]
    public async Task EntityDeduplication_FetchesArtistAndArtworkOnce_ForMultipleTracks()
    {
        // 3 tracks belonging to same artist and album
        string f1 = CreateTestMp3("track1.mp3", "Song 1", "Coldplay", "A Rush of Blood", 2002, 1);
        string f2 = CreateTestMp3("track2.mp3", "Song 2", "Coldplay", "A Rush of Blood", 2002, 2);
        string f3 = CreateTestMp3("track3.mp3", "Song 3", "Coldplay", "A Rush of Blood", 2002, 3);

        var t1 = new Track("t1", "Song 1", "art_cp", "Coldplay", "alb_rob", "A Rush of Blood", 200.0, f1, "Local", 1, 2002, DateTime.UtcNow);
        var t2 = new Track("t2", "Song 2", "art_cp", "Coldplay", "alb_rob", "A Rush of Blood", 210.0, f2, "Local", 2, 2002, DateTime.UtcNow);
        var t3 = new Track("t3", "Song 3", "art_cp", "Coldplay", "alb_rob", "A Rush of Blood", 220.0, f3, "Local", 3, 2002, DateTime.UtcNow);

        await _dbContext.UpsertArtistAsync(new Artist("art_cp", "Coldplay", null, null, true));
        await _dbContext.UpsertAlbumAsync(new Album("alb_rob", "A Rush of Blood", "art_cp", "Coldplay", 2002, null, "Local"));

        await _dbContext.UpsertTrackAsync(t1);
        await _dbContext.UpsertTrackAsync(t2);
        await _dbContext.UpsertTrackAsync(t3);

        var extIds = new ExternalIds(
            MusicBrainzId: "mb_rec_x",
            AdditionalIds: new Dictionary<string, string>
            {
                ["MusicBrainzReleaseGroupId"] = "mb_rg_rob",
                ["MusicBrainzArtistId"] = "mb_art_cp"
            });

        var mockMatcher = new Mock<ITrackMetadataMatcher>();
        mockMatcher.Setup(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Track t, CancellationToken ct) => new[]
            {
                new TrackMatchCandidate("MusicBrainz", extIds, 0.95, "Exact", new ExternalTrackMetadata(t.Title, "Coldplay", "A Rush of Blood", 2002, "Rock", t.TrackNumber, 1, t.DurationSeconds, null, extIds))
            });

        int artworkCalls = 0;
        var mockArtworkOrch = new Mock<IExternalArtworkOrchestrator>();
        mockArtworkOrch.Setup(a => a.ResolveAndCacheAlbumArtworkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ExternalIds>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref artworkCalls);
                return "cached_art_token.jpg";
            });

        int artistCalls = 0;
        var mockArtistService = new Mock<IArtistEnrichmentService>();
        mockArtistService.Setup(a => a.GetEnrichedArtistAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref artistCalls);
                return new EnrichedArtistProfile("mb_art_cp", "Coldplay", "Famous rock band", null, "Rock", "UK", 1996, null, Array.Empty<string>(), null, Array.Empty<string>(), ExternalIds.Empty, "TheAudioDB");
            });

        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var mockEditor = new Mock<ITrackMetadataEditor>();
        mockEditor.Setup(e => e.UpdateTrackMetadataAsync(It.IsAny<string>(), It.IsAny<TrackMetadataUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MetadataEditResult.Succeeded(FileWriteResult.Succeeded(""), DbSyncResult.Succeeded(t1)));

        var service = new SmartLibraryEnrichmentService(
            _dbContext,
            mockMatcher.Object,
            mockEditor.Object,
            mockArtworkOrch.Object,
            mockArtistService.Object,
            new Mock<IOnlineLyricsOrchestrator>().Object,
            settingsService,
            new Mock<ILibraryService>().Object,
            _artworkCacheManager,
            httpService);

        var summary = await service.RunEnrichmentScanAsync(dryRun: false);

        Assert.Equal(3, summary.TotalTracks);
        // Artwork and Artist must be resolved ONCE despite 3 tracks!
        Assert.Equal(1, artworkCalls);
        Assert.Equal(1, artistCalls);
    }

    // =================================================================
    // 5. REVIEW QUEUE: Manual Decision Application
    // =================================================================

    [Fact]
    public async Task ReviewQueue_ManualAccept_AppliesPlanSuccessfully()
    {
        string filePath = CreateTestMp3("manual_review.mp3", "Trouble", "Coldplay", "Parachutes", 2000, 6);
        var track = new Track("t_review", "Trouble", "a1", "Coldplay", "alb1", "Parachutes", 270.0, filePath, "Local", 6, 2000, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        var candidateMeta = new ExternalTrackMetadata("Trouble", "Coldplay", "Parachutes", 2000, "Alternative Rock", 6, 1, 270.0, "ISRC999", new ExternalIds(MusicBrainzId: "mb_tr_1"));
        var candidate = new TrackMatchCandidate("MusicBrainz", new ExternalIds(MusicBrainzId: "mb_tr_1"), 0.75, "Probable match", candidateMeta);

        var plan = new TrackEnrichmentExecutionPlan
        {
            TrackId = track.Id,
            TrackUri = track.SourceUri,
            LocalTitle = track.Title,
            LocalArtist = track.ArtistName,
            LocalAlbum = track.AlbumTitle,
            LocalDurationSeconds = track.DurationSeconds,
            SelectedCandidate = candidate,
            Confidence = 0.75,
            Status = EnrichmentTrackStatus.NeedsReview,
            PlannedActions = EnrichmentActions.WriteGenre,
            ProposedUpdate = new TrackMetadataUpdate(Genre: "Alternative Rock")
        };

        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var mockEditor = new Mock<ITrackMetadataEditor>();
        mockEditor.Setup(e => e.UpdateTrackMetadataAsync(track.Id, plan.ProposedUpdate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MetadataEditResult.Succeeded(FileWriteResult.Succeeded(filePath), DbSyncResult.Succeeded(track)));

        var service = new SmartLibraryEnrichmentService(
            _dbContext,
            new Mock<ITrackMetadataMatcher>().Object,
            mockEditor.Object,
            new Mock<IExternalArtworkOrchestrator>().Object,
            new Mock<IArtistEnrichmentService>().Object,
            new Mock<IOnlineLyricsOrchestrator>().Object,
            settingsService,
            new Mock<ILibraryService>().Object,
            _artworkCacheManager,
            httpService);

        var result = await service.ApplySinglePlanAsync(plan);

        Assert.True(result.Success);
        mockEditor.Verify(e => e.UpdateTrackMetadataAsync(track.Id, plan.ProposedUpdate, It.IsAny<CancellationToken>()), Times.Once);

        var state = await _dbContext.GetEnrichmentStateRecordAsync(track.Id);
        Assert.NotNull(state);
        Assert.Equal((int)EnrichmentTrackStatus.EnrichedSuccessfully, state.Value.Status);
    }

    // =================================================================
    // 6. EXCLUSION PERSISTENCE: Never Ask Again
    // =================================================================

    [Fact]
    public async Task ReviewQueue_NeverAskAgain_PersistsExclusionAndSkipsOnFutureScans()
    {
        string filePath = CreateTestMp3("exclude_test.mp3", "Obscure Track", "Unknown Artist", "Demo");
        var track = new Track("t_excl", "Obscure Track", "a1", "Unknown Artist", "alb1", "Demo", 150.0, filePath, "Local", 1, 0, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var mockMatcher = new Mock<ITrackMetadataMatcher>();

        var service = new SmartLibraryEnrichmentService(
            _dbContext,
            mockMatcher.Object,
            new Mock<ITrackMetadataEditor>().Object,
            new Mock<IExternalArtworkOrchestrator>().Object,
            new Mock<IArtistEnrichmentService>().Object,
            new Mock<IOnlineLyricsOrchestrator>().Object,
            settingsService,
            new Mock<ILibraryService>().Object,
            _artworkCacheManager,
            httpService);

        // User marks Never Ask Again
        await service.RejectOrSkipTrackAsync(track.Id, neverAskAgain: true);

        var state = await _dbContext.GetEnrichmentStateRecordAsync(track.Id);
        Assert.NotNull(state);
        Assert.Equal((int)EnrichmentTrackStatus.NeverAskAgain, state.Value.Status);

        // Run next library scan -> Track must be skipped immediately without matching!
        var summary = await service.RunEnrichmentScanAsync(dryRun: false);

        mockMatcher.Verify(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =================================================================
    // 7. MULTI-DIMENSIONAL COMPLETENESS: Artwork Only Plan
    // =================================================================

    [Fact]
    public async Task MultiDimensionalCompleteness_MissingArtworkOnly_GeneratesArtworkOnlyAction()
    {
        // Track has complete tags (title, artist, album, year, track number), but no album artwork
        string filePath = CreateTestMp3("complete_tags.mp3", "Clocks", "Coldplay", "A Rush of Blood", 2002, 2);
        var track = new Track("t_comp", "Clocks", "art_cp", "Coldplay", "alb_no_art", "A Rush of Blood", 307.0, filePath, "Local", 2, 2002, DateTime.UtcNow);
        await _dbContext.UpsertArtistAsync(new Artist("art_cp", "Coldplay", "Rock band", "art_tok.jpg", true));
        await _dbContext.UpsertAlbumAsync(new Album("alb_no_art", "A Rush of Blood", "art_cp", "Coldplay", 2002, null, "Local")); // null artwork
        await _dbContext.UpsertTrackAsync(track);

        var extIds = new ExternalIds(MusicBrainzId: "mb_clocks_1");
        var candidateMeta = new ExternalTrackMetadata("Clocks", "Coldplay", "A Rush of Blood", 2002, "Rock", 2, 1, 307.0, null, extIds);
        var candidate = new TrackMatchCandidate("MusicBrainz", extIds, 0.96, "Exact", candidateMeta);

        var mockMatcher = new Mock<ITrackMetadataMatcher>();
        mockMatcher.Setup(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candidate });

        var mockArtworkOrch = new Mock<IExternalArtworkOrchestrator>();
        mockArtworkOrch.Setup(a => a.ResolveAndCacheAlbumArtworkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ExternalIds>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("cached_cover.jpg");

        // Write a test image file into the cache root so it can be read
        string cacheCoverFile = Path.Combine(_artworkCacheManager.CacheRoot, "cached_cover.jpg");
        File.WriteAllBytes(cacheCoverFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });

        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var service = new SmartLibraryEnrichmentService(
            _dbContext,
            mockMatcher.Object,
            new Mock<ITrackMetadataEditor>().Object,
            mockArtworkOrch.Object,
            new Mock<IArtistEnrichmentService>().Object,
            new Mock<IOnlineLyricsOrchestrator>().Object,
            settingsService,
            new Mock<ILibraryService>().Object,
            _artworkCacheManager,
            httpService);

        var plan = await service.BuildPlanForTrackAsync(track, settingsService.CurrentSettings);

        Assert.Equal(EnrichmentTrackStatus.SafeReadyToApply, plan.Status);
        Assert.True(plan.PlannedActions.HasFlag(EnrichmentActions.WriteArtwork));
        // Tags are already complete, so tags should not be marked for overwrite
        Assert.False(plan.PlannedActions.HasFlag(EnrichmentActions.WriteTitle));
        Assert.False(plan.PlannedActions.HasFlag(EnrichmentActions.WriteArtist));
        Assert.False(plan.PlannedActions.HasFlag(EnrichmentActions.WriteAlbum));
    }

    // =================================================================
    // 8. MULTI-DIMENSIONAL COMPLETENESS: Fully Complete Track
    // =================================================================

    [Fact]
    public async Task MultiDimensionalCompleteness_FullyCompleteTrack_IsMarkedAlreadyComplete()
    {
        // Track has complete tags, lyrics, album has artwork, artist has bio & photo
        string filePath = CreateTestMp3("all_complete.mp3", "Viva La Vida", "Coldplay", "Viva La Vida", 2008, 7);
        using (var tag = TagLib.File.Create(filePath))
        {
            tag.Tag.Lyrics = "I used to roll the dice\nFeel the fear in my enemy's eyes";
            tag.Save();
        }
        var track = new Track("t_all_comp", "Viva La Vida", "art_cp", "Coldplay", "alb_art", "Viva La Vida", 242.0, filePath, "Local", 7, 2008, DateTime.UtcNow);
        await _dbContext.UpsertArtistAsync(new Artist("art_cp", "Coldplay", "Bio exists", "art_photo.jpg", true));
        await _dbContext.UpsertAlbumAsync(new Album("alb_art", "Viva La Vida", "art_cp", "Coldplay", 2008, "cover.jpg", "Local"));
        await _dbContext.UpsertTrackAsync(track);

        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var mockMatcher = new Mock<ITrackMetadataMatcher>();

        var service = new SmartLibraryEnrichmentService(
            _dbContext,
            mockMatcher.Object,
            new Mock<ITrackMetadataEditor>().Object,
            new Mock<IExternalArtworkOrchestrator>().Object,
            new Mock<IArtistEnrichmentService>().Object,
            new Mock<IOnlineLyricsOrchestrator>().Object,
            settingsService,
            new Mock<ILibraryService>().Object,
            _artworkCacheManager,
            httpService);

        var summary = await service.RunEnrichmentScanAsync(dryRun: true);

        Assert.Equal(1, summary.AlreadyCompleteCount);
        mockMatcher.Verify(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =================================================================
    // 9. RESILIENT PIPELINE: Lyrics Failure Does Not Abort Metadata
    // =================================================================

    [Fact]
    public async Task ResilientPipeline_LyricsFailure_DoesNotAbortMetadataOrArtworkPlanning()
    {
        string filePath = CreateTestMp3("lyrics_fail.mp3", "Scientist", "Coldplay", "A Rush of Blood", 2002, 5);
        var track = new Track("t_lyrics_fail", "Scientist", "a1", "Coldplay", "alb1", "A Rush of Blood", 309.0, filePath, "Local", 5, 2002, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        var extIds = new ExternalIds(MusicBrainzId: "mb_sci_1");
        var candidateMeta = new ExternalTrackMetadata("The Scientist", "Coldplay", "A Rush of Blood", 2002, "Rock", 5, 1, 309.0, null, extIds);
        var candidate = new TrackMatchCandidate("MusicBrainz", extIds, 0.95, "Exact", candidateMeta);

        var mockMatcher = new Mock<ITrackMetadataMatcher>();
        mockMatcher.Setup(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candidate });

        var mockLyricsOrch = new Mock<IOnlineLyricsOrchestrator>();
        mockLyricsOrch.Setup(l => l.FetchLyricsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<ExternalIds>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("LRCLIB service temporarily down"));

        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var service = new SmartLibraryEnrichmentService(
            _dbContext,
            mockMatcher.Object,
            new Mock<ITrackMetadataEditor>().Object,
            new Mock<IExternalArtworkOrchestrator>().Object,
            new Mock<IArtistEnrichmentService>().Object,
            mockLyricsOrch.Object,
            settingsService,
            new Mock<ILibraryService>().Object,
            _artworkCacheManager,
            httpService);

        var plan = await service.BuildPlanForTrackAsync(track, settingsService.CurrentSettings);

        // Plan succeeds even though lyrics failed
        Assert.Equal(EnrichmentTrackStatus.SafeReadyToApply, plan.Status);
        Assert.Null(plan.ProposedUpdate?.Lyrics);
        Assert.False(plan.PlannedActions.HasFlag(EnrichmentActions.WriteLyrics));
    }
}
