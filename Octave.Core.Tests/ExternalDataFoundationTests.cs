using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Octave.Core.Helpers;
using Octave.Core.Interfaces;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Cache;
using Octave.Core.Services.Database;
using Octave.Core.Services.External;
using Octave.Core.Services.Library;
using Octave.Core.Services.Metadata;
using Octave.Core.Services.Network;
using Xunit;

namespace Octave.Core.Tests;

public class ExternalDataFoundationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDbContext _dbContext;

    public ExternalDataFoundationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_ExtTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        string dbPath = Path.Combine(_tempDir, "test.db");
        _dbContext = new SqliteDbContext($"Data Source={dbPath}");
        _dbContext.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    // =================================================================
    // 1. HTTP SERVICE & RATE LIMITING TESTS (100% Mocked)
    // =================================================================

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> HandlerFunc { get; set; } =
            (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            HandlerFunc(request, cancellationToken);
    }

    [Fact]
    public async Task HttpService_GetJsonAsync_ReturnsDeserializedData_OnSuccess()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"name\":\"Queen\",\"country\":\"United Kingdom\"}")
                };
                return Task.FromResult(response);
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var registry = new ProviderRateLimiterRegistry();
        using var httpService = new HttpService(registry, httpClient);

        var result = await httpService.GetJsonAsync<Dictionary<string, string>>("https://api.example.com/artist/123", "test_provider");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Data);
        Assert.Equal("Queen", result.Data["name"]);
        Assert.Equal("United Kingdom", result.Data["country"]);
    }

    [Fact]
    public async Task HttpService_RetryAfter_And_TransientRetries_WorkGracefully()
    {
        int requestCount = 0;
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                requestCount++;
                if (requestCount == 1)
                {
                    var throttleResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    throttleResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
                    return Task.FromResult(throttleResponse);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("recovered")
                });
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var registry = new ProviderRateLimiterRegistry();
        using var httpService = new HttpService(registry, httpClient, maxRetries: 2);

        var result = await httpService.GetStringAsync("https://api.example.com/test", "test_provider");

        Assert.True(result.IsSuccess);
        Assert.Equal("recovered", result.Data);
        Assert.Equal(2, requestCount);
    }

    // =================================================================
    // 2. TWO-TIER CACHING TESTS (L1 Memory + L2 SQLite)
    // =================================================================

    [Fact]
    public async Task TwoTierCache_SetAndGet_HydratesL1_And_PersistsAcrossInstances()
    {
        var cacheInstance1 = new TwoTierExternalDataCache(_dbContext, TimeSpan.FromHours(1));

        var meta = new ExternalTrackMetadata(
            "Bohemian Rhapsody",
            "Queen",
            "A Night at the Opera",
            1975,
            "Rock",
            11,
            1,
            354.0,
            "GBUM71029603",
            new ExternalIds("mb_track_1", "spot_track_1", null, "GBUM71029603")
        );

        await cacheInstance1.SetAsync("track:queen:bohemian", meta);

        // L1 Cache Hit
        var l1Result = await cacheInstance1.GetAsync<ExternalTrackMetadata>("track:queen:bohemian");
        Assert.NotNull(l1Result);
        Assert.Equal("Bohemian Rhapsody", l1Result.Title);

        // New cache instance (Simulates app restart with empty L1 memory)
        var cacheInstance2 = new TwoTierExternalDataCache(_dbContext, TimeSpan.FromHours(1));
        var l2Result = await cacheInstance2.GetAsync<ExternalTrackMetadata>("track:queen:bohemian");

        Assert.NotNull(l2Result);
        Assert.Equal("Bohemian Rhapsody", l2Result.Title);
        Assert.Equal("Queen", l2Result.ArtistName);
        Assert.Equal("mb_track_1", l2Result.ExternalIds.MusicBrainzId);
    }

    [Fact]
    public async Task TwoTierCache_ExpiredEntries_ReturnNull()
    {
        var cache = new TwoTierExternalDataCache(_dbContext, TimeSpan.FromMilliseconds(50));

        await cache.SetAsync("temp:item", "hello_world", TimeSpan.FromMilliseconds(50));
        await Task.Delay(100);

        var result = await cache.GetAsync<string>("temp:item");
        Assert.Null(result);
    }

    // =================================================================
    // 3. DECOUPLED LYRICS ORCHESTRATION & COMPOSITE SERVICE TESTS
    // =================================================================

    [Fact]
    public async Task CompositeLyricsService_LocalFirst_FallsBackToOnlineOrchestrator()
    {
        var mockLocalLyrics = new LyricsService();
        var mockProvider = new Mock<IExternalLyricsProvider>();
        mockProvider.Setup(p => p.ProviderName).Returns("MockLrcLib");
        mockProvider.Setup(p => p.IsEnabled).Returns(true);
        mockProvider.Setup(p => p.Priority).Returns(1);

        var syncedLines = new List<LyricLine>
        {
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), "Is this the real life?"),
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), "Is this just fantasy?")
        };

        mockProvider.Setup(p => p.FetchLyricsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<ExternalIds>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalLyricsResult("tr1", LyricsState.Synced, syncedLines, null, "MockLrcLib"));

        var cache = new TwoTierExternalDataCache(_dbContext);
        var onlineOrchestrator = new OnlineLyricsOrchestrator(new[] { mockProvider.Object }, cache);
        var compositeService = new CompositeLyricsService(mockLocalLyrics, onlineOrchestrator);

        var track = new Track("tr1", "Bohemian Rhapsody", "ar1", "Queen", "al1", "A Night at the Opera", 354, "C:/music/fake_nonexistent.flac", "Local", 11, 1975, DateTime.UtcNow);

        var result = await compositeService.GetLyricsAsync(track);

        Assert.Equal(LyricsState.Synced, result.State);
        Assert.NotNull(result.SyncedLines);
        Assert.Equal(2, result.SyncedLines.Count);
        Assert.Equal("Is this the real life?", result.SyncedLines[0].Text);

        // Verify result was stored in cache
        var cached = await cache.GetAsync<LyricsData>("lyrics:queen:bohemian rhapsody");
        Assert.NotNull(cached);
        Assert.Equal(LyricsState.Synced, cached.State);
    }

    // =================================================================
    // 4. METADATA CANDIDATE SEARCH & RANKING TESTS
    // =================================================================

    [Fact]
    public async Task ExternalMetadataOrchestrator_RanksCandidatesByConfidence()
    {
        var mockProvider1 = new Mock<IExternalMetadataProvider>();
        mockProvider1.Setup(p => p.ProviderName).Returns("Provider1");
        mockProvider1.Setup(p => p.IsEnabled).Returns(true);
        mockProvider1.Setup(p => p.Priority).Returns(1);

        var metaLow = new ExternalTrackMetadata("Time", "Pink Floyd", "Dark Side", 1973, "Rock", 4, 1, 413, null, ExternalIds.Empty);
        var candidateLow = new TrackMatchCandidate("Provider1", ExternalIds.Empty, 0.75, "Fuzzy match", metaLow);

        mockProvider1.Setup(p => p.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candidateLow });

        var mockProvider2 = new Mock<IExternalMetadataProvider>();
        mockProvider2.Setup(p => p.ProviderName).Returns("Provider2");
        mockProvider2.Setup(p => p.IsEnabled).Returns(true);
        mockProvider2.Setup(p => p.Priority).Returns(2);

        var metaHigh = new ExternalTrackMetadata("Time", "Pink Floyd", "The Dark Side of the Moon", 1973, "Progressive Rock", 4, 1, 413, "GBAYE7300040", new ExternalIds("mb_time"));
        var candidateHigh = new TrackMatchCandidate("Provider2", new ExternalIds("mb_time"), 0.98, "Exact ISRC + Duration match", metaHigh);

        mockProvider2.Setup(p => p.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candidateHigh });

        var cache = new TwoTierExternalDataCache(_dbContext);
        var orchestrator = new ExternalMetadataOrchestrator(new[] { mockProvider1.Object, mockProvider2.Object }, cache);

        var results = await orchestrator.SearchTrackCandidatesAsync("Time", "Pink Floyd", "Dark Side", 413);

        Assert.Equal(2, results.Count);
        // Candidate with 0.98 confidence should rank first
        Assert.Equal(0.98, results[0].Confidence);
        Assert.Equal("Provider2", results[0].ProviderName);
        Assert.Equal(0.75, results[1].Confidence);
    }

    // =================================================================
    // 5. EXTERNAL ARTWORK & ARTIST IMAGE ORCHESTRATOR TESTS
    // =================================================================

    [Fact]
    public async Task ExternalArtworkOrchestrator_FetchesAndCachesImageToken()
    {
        var mockArtworkProvider = new Mock<IExternalAlbumArtworkProvider>();
        mockArtworkProvider.Setup(p => p.ProviderName).Returns("CoverArtArchive");
        mockArtworkProvider.Setup(p => p.IsEnabled).Returns(true);
        mockArtworkProvider.Setup(p => p.Priority).Returns(1);
        mockArtworkProvider.Setup(p => p.SearchAlbumArtworkUrlsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ExternalIds>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "https://coverart.example.com/album.jpg" });

        var mockHttpHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                byte[] fakeImageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 }; // JPEG header bytes
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(fakeImageBytes)
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return Task.FromResult(response);
            }
        };

        var httpClient = new HttpClient(mockHttpHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var artworkCacheManager = new ArtworkCacheManager(_tempDir);
        var cache = new TwoTierExternalDataCache(_dbContext);

        var orchestrator = new ExternalArtworkOrchestrator(
            new[] { mockArtworkProvider.Object },
            Array.Empty<IExternalArtistImageProvider>(),
            httpService,
            artworkCacheManager,
            cache);

        var token = await orchestrator.ResolveAndCacheAlbumArtworkAsync("A Night at the Opera", "Queen");

        Assert.NotNull(token);
        Assert.StartsWith("ArtworkCache/", token);
        Assert.EndsWith(".jpg", token);

        // Verify the file was written to the cache directory
        string absolutePath = Path.Combine(_tempDir, token.Replace("ArtworkCache/", ""));
        Assert.True(File.Exists(absolutePath));
    }
}
