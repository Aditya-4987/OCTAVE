using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;
using Octave.Core.Services.Cache;
using Octave.Core.Services.Database;
using Octave.Core.Services.External;
using Octave.Core.Services.External.Artist;
using Octave.Core.Services.External.Lyrics;
using Octave.Core.Services.External.MusicBrainz;
using Octave.Core.Services.Metadata;
using Octave.Core.Services.Network;
using Xunit;

namespace Octave.Core.Tests;

public class ExternalDataHardeningTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDbContext _dbContext;

    public ExternalDataHardeningTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_HardeningTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _dbContext = new SqliteDbContext($"Data Source={_dbPath}");
        _dbContext.InitializeAsync().GetAwaiter().GetResult();
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

    // =================================================================
    // 1. CACHE HIT
    // =================================================================

    [Fact]
    public async Task CacheHit_ReturnsCachedValueImmediately()
    {
        var cache = new TwoTierExternalDataCache(_dbContext);
        var meta = new ExternalTrackMetadata("Song A", "Artist A", "Album A", 2024, "Rock", 1, 1, 200.0, "US123", ExternalIds.Empty);

        await cache.SetAsync("meta:track:mb:123", meta, TimeSpan.FromDays(30));

        var retrieved = await cache.GetAsync<ExternalTrackMetadata>("meta:track:mb:123");

        Assert.NotNull(retrieved);
        Assert.Equal("Song A", retrieved.Title);
        Assert.Equal("Artist A", retrieved.ArtistName);
    }

    // =================================================================
    // 2. CACHE MISS
    // =================================================================

    [Fact]
    public async Task CacheMiss_ReturnsDefaultNull()
    {
        var cache = new TwoTierExternalDataCache(_dbContext);

        var retrieved = await cache.GetAsync<ExternalTrackMetadata>("meta:track:nonexistent:999");

        Assert.Null(retrieved);
    }

    // =================================================================
    // 3. EXPIRED CACHE
    // =================================================================

    [Fact]
    public async Task ExpiredCache_DoesNotReturnDuringNormalLookup()
    {
        var cache = new TwoTierExternalDataCache(_dbContext);
        var meta = new ExternalTrackMetadata("Expired Song", "Artist", "Album", 2020, "Pop", 1, 1, 180, null, ExternalIds.Empty);

        // Set with -1 second TTL (already expired)
        await cache.SetAsync("meta:track:expired:1", meta, TimeSpan.FromSeconds(-1));

        // Normal GetAsync should return null because entry is expired
        var retrieved = await cache.GetAsync<ExternalTrackMetadata>("meta:track:expired:1");
        Assert.Null(retrieved);

        // GetWithMetadataAsync with allowStale: true should return the stale entry
        var stale = await cache.GetWithMetadataAsync<ExternalTrackMetadata>("meta:track:expired:1", allowStale: true);
        Assert.NotNull(stale);
        Assert.True(stale.IsExpired);
        Assert.Equal("Expired Song", stale.Value.Title);
    }

    // =================================================================
    // 4. OFFLINE REQUEST WITH CACHED RESULT (0 Network Calls)
    // =================================================================

    [Fact]
    public async Task OfflineRequest_WithCachedResult_ServesDataWithZeroNetworkCalls()
    {
        int networkCalls = 0;
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Interlocked.Increment(ref networkCalls);
                throw new HttpRequestException("Offline / Network unavailable");
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var cache = new TwoTierExternalDataCache(_dbContext);

        // Pre-populate cache
        var meta = new ExternalTrackMetadata("Offline Hit", "Artist", "Album", 2024, "Rock", 1, 1, 220, null, new ExternalIds("rec-offline"));
        await cache.SetAsync("meta:track:musicbrainz:rec-offline", meta, TimeSpan.FromDays(30));

        var orchestrator = new ExternalMetadataOrchestrator(new[] { new MusicBrainzMetadataProvider(httpService, rateRegistry) }, cache);

        var result = await orchestrator.GetTrackMetadataAsync("MusicBrainz", "rec-offline");

        Assert.NotNull(result);
        Assert.Equal("Offline Hit", result.Title);
        Assert.Equal(0, networkCalls);
    }

    // =================================================================
    // 5. OFFLINE REQUEST WITHOUT CACHED RESULT (Graceful without exceptions)
    // =================================================================

    [Fact]
    public async Task OfflineRequest_WithoutCachedResult_HandlesGracefullyWithoutThrowing()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => throw new HttpRequestException("Offline / DNS failure")
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var cache = new TwoTierExternalDataCache(_dbContext);

        var orchestrator = new ExternalMetadataOrchestrator(new[] { new MusicBrainzMetadataProvider(httpService, rateRegistry) }, cache);

        var result = await orchestrator.GetTrackMetadataAsync("MusicBrainz", "rec-uncached-offline");

        // Must return null without throwing exception to UI
        Assert.Null(result);
    }

    // =================================================================
    // 6. CONCURRENT IDENTICAL REQUESTS (Single-Flight / No Stampede)
    // =================================================================

    [Fact]
    public async Task ConcurrentIdenticalRequests_ExecutesOnlySingleProviderCall()
    {
        int networkCalls = 0;
        string lrclibJson = "{\"id\":99,\"plainLyrics\":\"Concurrent test lyrics\"}";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = async (req, ct) =>
            {
                Interlocked.Increment(ref networkCalls);
                await Task.Delay(50, ct); // Simulate in-flight duration
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(lrclibJson)
                };
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var cache = new TwoTierExternalDataCache(_dbContext);

        var provider = new LrcLibLyricsProvider(httpService, rateRegistry);
        var orchestrator = new OnlineLyricsOrchestrator(new[] { provider }, cache);

        // Fire 10 parallel identical requests
        var tasks = new List<Task<LyricsData>>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(orchestrator.FetchLyricsAsync("Stampede Track", "Stampede Artist"));
        }

        var results = await Task.WhenAll(tasks);

        foreach (var r in results)
        {
            Assert.Equal(LyricsState.Unsynced, r.State);
            Assert.Equal("Concurrent test lyrics", r.PlainText);
        }

        // Only 1 network call was dispatched
        Assert.Equal(1, networkCalls);
    }

    // =================================================================
    // 7. PROVIDER FAILURE WITH STALE CACHE (Graceful Fallback)
    // =================================================================

    [Fact]
    public async Task ProviderFailure_WithStaleCache_FallsBackToStaleDataGracefully()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var cache = new TwoTierExternalDataCache(_dbContext);

        // Pre-populate with an expired (stale) entry
        var staleMeta = new ExternalTrackMetadata("Stale Saved Title", "Artist", "Album", 2010, "Rock", 1, 1, 210, null, new ExternalIds("rec-stale"));
        await cache.SetAsync("meta:track:musicbrainz:rec-stale", staleMeta, TimeSpan.FromSeconds(-10));

        var orchestrator = new ExternalMetadataOrchestrator(new[] { new MusicBrainzMetadataProvider(httpService, rateRegistry) }, cache);

        var result = await orchestrator.GetTrackMetadataAsync("MusicBrainz", "rec-stale");

        Assert.NotNull(result);
        Assert.Equal("Stale Saved Title", result.Title);
    }

    // =================================================================
    // 8. APPLICATION RESTART PERSISTENCE (L2 SQLite Cache)
    // =================================================================

    [Fact]
    public async Task ApplicationRestartPersistence_SurvivesNewCacheAndDbInstances()
    {
        var profile = new EnrichedArtistProfile(
            "ar-restart-1",
            "Pink Floyd",
            "Legendary British rock band",
            "ArtworkCache/abc.jpg",
            "Progressive Rock",
            "United Kingdom",
            1965,
            null,
            new[] { "Rock", "Psychedelic" },
            new Dictionary<string, string> { ["Website"] = "http://pinkfloyd.com" },
            null,
            new ExternalIds("mb-pf-1"),
            "TheAudioDB");

        // 1. Write to first cache instance
        var cache1 = new TwoTierExternalDataCache(_dbContext);
        await cache1.SetAsync("artist_enrich:mbid:mb-pf-1", profile, TimeSpan.FromDays(30));

        // 2. Simulate complete application restart by creating brand new DbContext and Cache instances
        var restartDbContext = new SqliteDbContext($"Data Source={_dbPath}");
        await restartDbContext.InitializeAsync();
        var cache2 = new TwoTierExternalDataCache(restartDbContext);

        // 3. Read from second cache instance
        var loadedProfile = await cache2.GetAsync<EnrichedArtistProfile>("artist_enrich:mbid:mb-pf-1");

        Assert.NotNull(loadedProfile);
        Assert.Equal("Pink Floyd", loadedProfile.Name);
        Assert.Equal("Legendary British rock band", loadedProfile.Biography);
        Assert.Equal("ArtworkCache/abc.jpg", loadedProfile.LocalImageToken);
        Assert.Equal(1965, loadedProfile.FormedYear);
        Assert.Equal("http://pinkfloyd.com", loadedProfile.ExternalLinks!["Website"]);
    }
}
