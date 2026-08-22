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

    // =================================================================
    // 10. EXACT-ID HARD SAFETY GATE (SLE-01 — Batch 2)
    // =================================================================

    [Fact]
    public async Task HardSafetyGates_ExactMbidOnFileTags_PassesDespiteGarbageFuzzyFields()
    {
        // Regression proof for SLE-01: the gate used to compare the path-hash Track.Id
        // against a candidate MBID (never true), so known-correct-MBID tracks were
        // demoted to NeedsReview. With the fix, an exact MBID on the FILE satisfies
        // every fuzzy gate outright.
        string filePath = CreateTestMp3("mbid_gate.mp3", "Qwerty Wrong Title", "Zzzz Wrong Artist", "Wrong Album", 1999, 1);
        using (var tag = TagLib.File.Create(filePath))
        {
            tag.Tag.MusicBrainzTrackId = "mb_gate_exact_1";
            tag.Save();
        }

        var track = new Track("t_mbid_gate", "Qwerty Wrong Title", "a9", "Zzzz Wrong Artist", "alb9", "Wrong Album", 111.0, filePath, "Local", 1, 1999, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        // Candidate matches ONLY by MBID; every fuzzy dimension is garbage and would
        // fail Gates 1-4 (version, title, artist, duration) if they were consulted.
        var candidateMeta = new ExternalTrackMetadata(
            "Completely Different Song", "Unrelated Artist", "Other Album", 1970, null, 7, 2, 999.0, null,
            new ExternalIds(MusicBrainzId: "mb_gate_exact_1"));
        var candidate = new TrackMatchCandidate("MusicBrainz", new ExternalIds(MusicBrainzId: "mb_gate_exact_1"), 0.99, "Mocked score", candidateMeta);

        var mockMatcher = new Mock<ITrackMetadataMatcher>();
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

        var summary = await service.RunEnrichmentScanAsync(dryRun: true);

        Assert.Equal(1, summary.SafeReadyCount);
        Assert.Equal(0, summary.NeedsReviewCount);

        var persisted = await _dbContext.GetEnrichmentStateRecordAsync(track.Id);
        Assert.True(persisted.HasValue);
        Assert.Equal((int)EnrichmentTrackStatus.SafeReadyToApply, persisted.Value.Status);
        Assert.True(persisted.Value.HardGatesPassed);
    }

    // =================================================================
    // 11. WRITE-POLICY SETTINGS (INT-02 / SLE-02 / INT-03 — Batch 3)
    // =================================================================

    private const EnrichmentActions AllMetadataTagWrites =
        EnrichmentActions.WriteTitle | EnrichmentActions.WriteArtist | EnrichmentActions.WriteAlbum |
        EnrichmentActions.WriteGenre | EnrichmentActions.WriteYear | EnrichmentActions.WriteTrackNumber |
        EnrichmentActions.WriteDiscNumber;

    private async Task<(SmartLibraryEnrichmentService Service, ExternalDataSettings Settings)> CreateServiceWithDefaultsAsync(
        ITrackMetadataMatcher matcher,
        IArtistEnrichmentService? artistService = null)
    {
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpClient = new HttpClient(new MockHttpMessageHandler());
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var service = new SmartLibraryEnrichmentService(
            _dbContext,
            matcher,
            new Mock<ITrackMetadataEditor>().Object,
            new Mock<IExternalArtworkOrchestrator>().Object,
            artistService ?? new Mock<IArtistEnrichmentService>().Object,
            new Mock<IOnlineLyricsOrchestrator>().Object,
            settingsService,
            new Mock<ILibraryService>().Object,
            _artworkCacheManager,
            httpService);

        return (service, settingsService.CurrentSettings);
    }

    // Local fields are gate-clean (title/artist/album identical to the candidate) but
    // carry a REAL year and a MISSING genre — so genre exercises fill-missing while
    // year exercises overwrite-permission without tripping any safety gate.
    private async Task<Track> CreateGateCleanTrackAsync(string fileName)
    {
        string filePath = CreateTestMp3(fileName, "Fix You", "Coldplay", "Parachutes", 1999, 4);
        var track = new Track("t_" + fileName, "Fix You", "a1", "Coldplay", "alb1", "Parachutes", 310.0, filePath, "Local", 4, 1999, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);
        return track;
    }

    private static TrackMatchCandidate CreateGateCleanCandidate() =>
        new("MusicBrainz",
            new ExternalIds(MusicBrainzId: "mb_policy_1"),
            0.95,
            "Exact match",
            new ExternalTrackMetadata("Fix You", "Coldplay", "Parachutes", 2005, "Rock", 4, 2, 310.0, null,
                new ExternalIds(MusicBrainzId: "mb_policy_1")));

    private static ITrackMetadataMatcher MatcherReturning(TrackMatchCandidate candidate)
    {
        var mockMatcher = new Mock<ITrackMetadataMatcher>();
        mockMatcher.Setup(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candidate });
        return mockMatcher.Object;
    }

    [Fact]
    public async Task WritePolicy_AutoFillMissingMetadataOff_ProposesNoMetadataTagWrites()
    {
        // INT-02 regression: the master autofill toggle (default false) used to do
        // nothing — scans wrote tag text even with it off.
        var track = await CreateGateCleanTrackAsync("policy_autofill_off.mp3");
        var (service, settings) = await CreateServiceWithDefaultsAsync(MatcherReturning(CreateGateCleanCandidate()));
        Assert.False(settings.AutoFillMissingMetadata); // guard: default really is off

        var plan = await service.BuildPlanForTrackAsync(track, settings);

        Assert.Equal(EnrichmentTrackStatus.SafeReadyToApply, plan.Status);
        Assert.Equal(EnrichmentActions.None, plan.PlannedActions & AllMetadataTagWrites);
    }

    [Fact]
    public async Task WritePolicy_NeverWriteAutomatically_SuppressesWritesEvenWhenAutoFillOn()
    {
        var track = await CreateGateCleanTrackAsync("policy_never.mp3");
        var (service, defaults) = await CreateServiceWithDefaultsAsync(MatcherReturning(CreateGateCleanCandidate()));
        var settings = defaults.Clone();
        settings.AutoFillMissingMetadata = true;
        settings.WritePolicy = MetadataWritePolicy.NeverWriteAutomatically;

        var plan = await service.BuildPlanForTrackAsync(track, settings);

        Assert.Equal(EnrichmentActions.None, plan.PlannedActions & AllMetadataTagWrites);
    }

    [Fact]
    public async Task WritePolicy_FillMissing_FillsGenreButNeverOverwritesRealYear()
    {
        // Default policy + explicit replace permission: permission alone is not
        // sufficient under a fill-missing policy.
        var track = await CreateGateCleanTrackAsync("policy_fill_missing.mp3");
        var (service, defaults) = await CreateServiceWithDefaultsAsync(MatcherReturning(CreateGateCleanCandidate()));
        var settings = defaults.Clone();
        settings.AutoFillMissingMetadata = true;
        settings.ScanOnlyMissingMetadata = false; // let the write policy itself decide
        settings.ReplaceExistingMetadata = true;

        var plan = await service.BuildPlanForTrackAsync(track, settings);

        // Genre is missing → filled. The fixture tag carries no disc number → filled
        // from the candidate too. The real year (1999) is untouched — the point.
        Assert.Equal(EnrichmentActions.WriteGenre | EnrichmentActions.WriteDiscNumber, plan.PlannedActions & AllMetadataTagWrites);
        Assert.Equal("Rock", plan.ProposedUpdate?.Genre);
    }

    [Fact]
    public async Task WritePolicy_AlwaysPreferOnline_WithReplacePermission_OverwritesRealYear()
    {
        var track = await CreateGateCleanTrackAsync("policy_prefer_online.mp3");
        var (service, defaults) = await CreateServiceWithDefaultsAsync(MatcherReturning(CreateGateCleanCandidate()));
        var settings = defaults.Clone();
        settings.AutoFillMissingMetadata = true;
        settings.ScanOnlyMissingMetadata = false;
        settings.ReplaceExistingMetadata = true;
        settings.WritePolicy = MetadataWritePolicy.AlwaysPreferOnline;

        var plan = await service.BuildPlanForTrackAsync(track, settings);

        // Minimal-diff: only fields whose candidate value actually differs from the
        // file (year 1999→2005, genre missing→Rock, disc missing→2) are flagged —
        // identical title/artist/album/track-number values are not restamped.
        Assert.Equal(EnrichmentActions.WriteYear | EnrichmentActions.WriteGenre | EnrichmentActions.WriteDiscNumber,
            plan.PlannedActions & AllMetadataTagWrites);
        Assert.Equal(2005, plan.ProposedUpdate?.Year);
    }

    [Fact]
    public async Task WritePolicy_OverwritePolicies_WithoutReplacePermission_StillFillMissingOnly()
    {
        var track = await CreateGateCleanTrackAsync("policy_no_replace_perm.mp3");
        var (service, defaults) = await CreateServiceWithDefaultsAsync(MatcherReturning(CreateGateCleanCandidate()));
        var settings = defaults.Clone();
        settings.AutoFillMissingMetadata = true;
        settings.ScanOnlyMissingMetadata = false;
        settings.WritePolicy = MetadataWritePolicy.AlwaysPreferOnline;
        // ReplaceExistingMetadata stays false

        var plan = await service.BuildPlanForTrackAsync(track, settings);

        // Fill-missing only: genre + disc (both missing locally); the real year and
        // every identical-value field stay untouched without replace permission.
        Assert.Equal(EnrichmentActions.WriteGenre | EnrichmentActions.WriteDiscNumber, plan.PlannedActions & AllMetadataTagWrites);
    }

    [Fact]
    public async Task DiscNumber_ExistingLocalValue_NotRestampedByFillMissingPolicy()
    {
        // New finding (this session): the disc write compared against nothing at all
        // (Track has no DiscNumber column), so every scan restamped existing disc
        // numbers whenever the candidate carried one. The fix reads the tag value.
        string filePath = CreateTestMp3("disc_existing.mp3", "Fix You", "Coldplay", "Parachutes", 1999, 4);
        using (var tagFile = TagLib.File.Create(filePath))
        {
            tagFile.Tag.Disc = 2;
            tagFile.Save();
        }
        var track = new Track("t_disc_existing", "Fix You", "a1", "Coldplay", "alb1", "Parachutes", 310.0, filePath, "Local", 4, 1999, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        var (service, defaults) = await CreateServiceWithDefaultsAsync(MatcherReturning(CreateGateCleanCandidate()));
        var settings = defaults.Clone();
        settings.AutoFillMissingMetadata = true; // fill-missing policy active
        settings.ScanOnlyMissingMetadata = false; // isolate the disc decision from the completeness filter

        var plan = await service.BuildPlanForTrackAsync(track, settings);

        Assert.Equal(EnrichmentActions.None, plan.PlannedActions & EnrichmentActions.WriteDiscNumber);
    }

    [Fact]
    public async Task DiscNumber_MissingLocalValue_IsFilledWhenAutoFillOn()
    {
        var track = await CreateGateCleanTrackAsync("disc_missing.mp3"); // no disc on tag
        var (service, defaults) = await CreateServiceWithDefaultsAsync(MatcherReturning(CreateGateCleanCandidate()));
        var settings = defaults.Clone();
        settings.AutoFillMissingMetadata = true;
        settings.ScanOnlyMissingMetadata = false;

        var plan = await service.BuildPlanForTrackAsync(track, settings);

        Assert.Equal(EnrichmentActions.WriteDiscNumber, plan.PlannedActions & EnrichmentActions.WriteDiscNumber);
        Assert.Equal(2, plan.ProposedUpdate?.DiscNumber);
    }

    [Fact]
    public async Task ScanOnlyMissingMetadata_On_CompleteTracksNotRewritten_EvenUnderReplacePolicies()
    {
        // SLE-02: ScanOnlyMissingMetadata was computed but never read. With it ON
        // (default), a fully-tagged track must not be rewritten even when both
        // replacement permissions are granted.
        string filePath = CreateTestMp3("scan_only_meta_on.mp3", "Fix You", "Coldplay", "Parachutes", 1999, 4);
        using (var tagFile = TagLib.File.Create(filePath))
        {
            tagFile.Tag.Genres = new[] { "Britpop" };
            tagFile.Save();
        }
        var track = new Track("t_scan_only_on", "Fix You", "a1", "Coldplay", "alb1", "Parachutes", 310.0, filePath, "Local", 4, 1999, DateTime.UtcNow, "Britpop");
        await _dbContext.UpsertTrackAsync(track);

        var (service, defaults) = await CreateServiceWithDefaultsAsync(MatcherReturning(CreateGateCleanCandidate()));
        var settings = defaults.Clone();
        settings.AutoFillMissingMetadata = true;
        settings.ReplaceExistingMetadata = true;
        settings.WritePolicy = MetadataWritePolicy.AlwaysPreferOnline;
        Assert.True(settings.ScanOnlyMissingMetadata); // guard: default really is on

        var plan = await service.BuildPlanForTrackAsync(track, settings);

        Assert.Equal(EnrichmentActions.None, plan.PlannedActions & AllMetadataTagWrites);

        // Turning the scan filter OFF widens consideration to complete tracks...
        // Minimal-diff: only Britpop→Rock (genre), 1999→2005 (year) and the missing
        // disc differ from the file; identical title/artist/album/track# are skipped.
        settings.ScanOnlyMissingMetadata = false;
        var widened = await service.BuildPlanForTrackAsync(track, settings);
        Assert.Equal(EnrichmentActions.WriteYear | EnrichmentActions.WriteGenre | EnrichmentActions.WriteDiscNumber,
            widened.PlannedActions & AllMetadataTagWrites);
    }

    [Fact]
    public async Task ArtistGate_CompleteArtist_SkipsReenrichment_EvenWithTogglesOn()
    {
        // INT-03 regression: `(!IsArtistComplete || EnableArtistEnrichment)` was
        // always-true because the toggle defaulted true — every scan re-enriched
        // every already-complete artist.
        string filePath = CreateTestMp3("artist_gate.mp3", "Fix You", "Coldplay", "Parachutes", 2000, 12);
        var track = new Track("t_artist_gate", "Fix You", "art_cp_done", "Coldplay", "alb1", "Parachutes", 310.0, filePath, "Local", 12, 2000, DateTime.UtcNow);
        await _dbContext.UpsertArtistAsync(new Artist("art_cp_done", "Coldplay", "Bio exists", "photo.jpg", true));
        await _dbContext.UpsertTrackAsync(track);

        var candidate = new TrackMatchCandidate(
            "MusicBrainz",
            new ExternalIds("mb_ag_1", AdditionalIds: new Dictionary<string, string> { ["MusicBrainzArtistId"] = "mb_art_cp_done" }),
            0.95,
            "Exact match",
            new ExternalTrackMetadata("Fix You", "Coldplay", "Parachutes", 2000, "Rock", 12, 2, 310.0, null,
                new ExternalIds("mb_ag_1")));

        var mockArtistService = new Mock<IArtistEnrichmentService>();

        var (service, defaults) = await CreateServiceWithDefaultsAsync(MatcherReturning(candidate), mockArtistService.Object);
        var settings = defaults.Clone();
        settings.AutoDownloadMissingArtistImages = true;   // both toggles ON:
        settings.EnableArtistEnrichment = true;            // the old gate still fired

        var plan = await service.BuildPlanForTrackAsync(track, settings);

        Assert.Equal(EnrichmentTrackStatus.SafeReadyToApply, plan.Status);
        mockArtistService.Verify(a => a.GetEnrichedArtistAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(plan.PlannedActions.HasFlag(EnrichmentActions.EnrichArtistBioAndPhoto));
    }

    [Fact]
    public async Task LyricsScan_ScanOnlyMissingLyricsOn_TrackWithLyrics_SkipsOnlineFetch()
    {
        // SLE-02: ScanOnlyMissingLyrics was computed but never read — lyrics were
        // re-fetched for tracks that already had them regardless of the setting.
        string filePath = CreateTestMp3("lyrics_scan.mp3", "Fix You", "Coldplay", "Parachutes", 2000, 7);
        using (var tagFile = TagLib.File.Create(filePath))
        {
            tagFile.Tag.Lyrics = "Lights will guide you home";
            tagFile.Save();
        }
        var track = new Track("t_lyrics_scan", "Fix You", "a1", "Coldplay", "alb1", "Parachutes", 310.0, filePath, "Local", 7, 2000, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        var mockLyricsOrch = new Mock<IOnlineLyricsOrchestrator>();
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, new HttpClient(new MockHttpMessageHandler()));
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var service = new SmartLibraryEnrichmentService(
            _dbContext,
            MatcherReturning(CreateGateCleanCandidate()),
            new Mock<ITrackMetadataEditor>().Object,
            new Mock<IExternalArtworkOrchestrator>().Object,
            new Mock<IArtistEnrichmentService>().Object,
            mockLyricsOrch.Object,
            settingsService,
            new Mock<ILibraryService>().Object,
            _artworkCacheManager,
            httpService);

        var settings = settingsService.CurrentSettings.Clone();
        settings.EnableOnlineLyrics = true;

        await service.BuildPlanForTrackAsync(track, settings); // ScanOnlyMissingLyrics=true (default)
        mockLyricsOrch.Verify(l => l.FetchLyricsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<ExternalIds>(), It.IsAny<CancellationToken>()), Times.Never);

        settings.ScanOnlyMissingLyrics = false;
        await service.BuildPlanForTrackAsync(track, settings);
        mockLyricsOrch.Verify(l => l.FetchLyricsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<ExternalIds>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
