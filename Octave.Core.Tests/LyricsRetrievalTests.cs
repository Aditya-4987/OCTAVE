using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Octave.Core.Models;
using Octave.Core.Services.Cache;
using Octave.Core.Services.Database;
using Octave.Core.Services.External;
using Octave.Core.Services.External.Lyrics;
using Octave.Core.Services.Metadata;
using Octave.Core.Services.Network;
using Xunit;

namespace Octave.Core.Tests;

public class LyricsRetrievalTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDbContext _dbContext;

    public LyricsRetrievalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_LyricsTests_" + Guid.NewGuid().ToString("N"));
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

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> HandlerFunc { get; set; } =
            (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            HandlerFunc(request, cancellationToken);
    }

    // =================================================================
    // 1. SYNCHRONIZED LYRICS RETRIEVAL
    // =================================================================

    [Fact]
    public async Task FetchLyricsAsync_SyncedLyrics_ReturnsParsedTimestampedLines()
    {
        string lrclibJson = @"
        {
            ""id"": 101,
            ""trackName"": ""Bohemian Rhapsody"",
            ""artistName"": ""Queen"",
            ""plainLyrics"": ""Is this the real life?\nIs this just fantasy?"",
            ""syncedLyrics"": ""[00:05.12] Is this the real life?\n[00:08.45] Is this just fantasy?""
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Assert.Contains("track_name=Bohemian", req.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(lrclibJson)
                });
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var provider = new LrcLibLyricsProvider(httpService, rateRegistry);
        var orchestrator = new OnlineLyricsOrchestrator(new[] { provider }, twoTierCache);

        var result = await orchestrator.FetchLyricsAsync("Bohemian Rhapsody", "Queen", "A Night at the Opera", 354.0);

        Assert.Equal(LyricsState.Synced, result.State);
        Assert.NotNull(result.SyncedLines);
        Assert.Equal(2, result.SyncedLines.Count);

        // Line 1: [00:05.12]
        Assert.Equal(TimeSpan.FromMilliseconds(5120), result.SyncedLines[0].Start);
        Assert.Equal("Is this the real life?", result.SyncedLines[0].Text);

        // Line 2: [00:08.45]
        Assert.Equal(TimeSpan.FromMilliseconds(8450), result.SyncedLines[1].Start);
        Assert.Equal("Is this just fantasy?", result.SyncedLines[1].Text);
    }

    // =================================================================
    // 2. ONLINE PLAIN LYRICS
    // =================================================================

    [Fact]
    public async Task FetchLyricsAsync_PlainLyricsOnly_ReturnsUnsyncedState()
    {
        string lrclibJson = @"
        {
            ""id"": 102,
            ""trackName"": ""Yesterday"",
            ""artistName"": ""The Beatles"",
            ""plainLyrics"": ""Yesterday, all my troubles seemed so far away\nNow it looks as though they're here to stay"",
            ""syncedLyrics"": null
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(lrclibJson)
            })
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var provider = new LrcLibLyricsProvider(httpService, rateRegistry);
        var orchestrator = new OnlineLyricsOrchestrator(new[] { provider }, twoTierCache);

        var result = await orchestrator.FetchLyricsAsync("Yesterday", "The Beatles");

        Assert.Equal(LyricsState.Unsynced, result.State);
        Assert.Null(result.SyncedLines);
        Assert.NotNull(result.PlainText);
        Assert.Contains("Yesterday, all my troubles", result.PlainText);
    }

    // =================================================================
    // 3. NO RESULT (404 NOT FOUND)
    // =================================================================

    [Fact]
    public async Task FetchLyricsAsync_NoResult_ReturnsUnavailableGracefully()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var provider = new LrcLibLyricsProvider(httpService, rateRegistry);
        var orchestrator = new OnlineLyricsOrchestrator(new[] { provider }, twoTierCache);

        var result = await orchestrator.FetchLyricsAsync("NonexistentSong123", "UnknownArtist");

        Assert.Equal(LyricsState.Unavailable, result.State);
        Assert.Null(result.SyncedLines);
        Assert.Null(result.PlainText);
    }

    // =================================================================
    // 4. PROVIDER FAILURE (500 ERROR)
    // =================================================================

    [Fact]
    public async Task FetchLyricsAsync_ProviderError_ReturnsUnavailableWithoutCrashing()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var provider = new LrcLibLyricsProvider(httpService, rateRegistry);
        var orchestrator = new OnlineLyricsOrchestrator(new[] { provider }, twoTierCache);

        var result = await orchestrator.FetchLyricsAsync("Radio Ga Ga", "Queen");

        Assert.Equal(LyricsState.Unavailable, result.State);
    }

    // =================================================================
    // 5. CANCELLATION
    // =================================================================

    [Fact]
    public async Task FetchLyricsAsync_Cancelled_ExitsCleanly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var provider = new LrcLibLyricsProvider(httpService, rateRegistry);
        var orchestrator = new OnlineLyricsOrchestrator(new[] { provider }, twoTierCache);

        var result = await orchestrator.FetchLyricsAsync("Song", "Artist", ct: cts.Token);

        Assert.Equal(LyricsState.Unavailable, result.State);
    }

    // =================================================================
    // 6. RAPID TRACK CHANGES & STALE RESPONSE PROTECTION
    // =================================================================

    [Fact]
    public async Task RapidTrackChanges_OnlyLatestTrackResultApplies()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = async (req, ct) =>
            {
                if (req.RequestUri!.ToString().Contains("Track1"))
                {
                    await Task.Delay(200, ct); // slow response
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":1,\"plainLyrics\":\"Lyrics 1\"}")
                    };
                }
                else
                {
                    await Task.Delay(20, ct); // fast response
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":2,\"plainLyrics\":\"Lyrics 2\"}")
                    };
                }
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var provider = new LrcLibLyricsProvider(httpService, rateRegistry);
        var orchestrator = new OnlineLyricsOrchestrator(new[] { provider }, twoTierCache);
        var compositeService = new CompositeLyricsService(new LyricsService(), orchestrator);

        var track1 = new Track("tr1", "Track1", "ar1", "Artist", "al1", "Album", 200, "C:/fake/1.mp3", "Local", 1, 2020, DateTime.UtcNow);
        var track2 = new Track("tr2", "Track2", "ar1", "Artist", "al1", "Album", 200, "C:/fake/2.mp3", "Local", 2, 2020, DateTime.UtcNow);

        // Simulate Track 1 requested, then user rapidly skips to Track 2
        using var cts1 = new CancellationTokenSource();
        var task1 = compositeService.GetLyricsAsync(track1, cts1.Token);

        // Cancel Track 1 upon skip
        cts1.Cancel();

        using var cts2 = new CancellationTokenSource();
        var task2 = compositeService.GetLyricsAsync(track2, cts2.Token);

        var result2 = await task2;
        Assert.Equal(LyricsState.Unsynced, result2.State);
        Assert.Equal("Lyrics 2", result2.PlainText);
        Assert.Equal("tr2", result2.TrackId);
    }

    // =================================================================
    // 7. CACHE HIT
    // =================================================================

    [Fact]
    public async Task FetchLyricsAsync_SecondCall_ReturnsCachedLyricsWithoutNetwork()
    {
        int networkCallCount = 0;
        string lrclibJson = @"
        {
            ""id"": 105,
            ""trackName"": ""Under Pressure"",
            ""artistName"": ""Queen"",
            ""plainLyrics"": ""Pressure pushing down on me\nPressing down on you"",
            ""syncedLyrics"": null
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Interlocked.Increment(ref networkCallCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(lrclibJson)
                });
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var provider = new LrcLibLyricsProvider(httpService, rateRegistry);
        var orchestrator = new OnlineLyricsOrchestrator(new[] { provider }, twoTierCache);

        // First call: fetches from provider & caches
        var res1 = await orchestrator.FetchLyricsAsync("Under Pressure", "Queen");
        Assert.Equal(LyricsState.Unsynced, res1.State);
        int callsAfterFirst = networkCallCount;

        // Second call: must hit Two-Tier cache
        var res2 = await orchestrator.FetchLyricsAsync("Under Pressure", "Queen");
        Assert.Equal(LyricsState.Unsynced, res2.State);
        Assert.Equal(res1.PlainText, res2.PlainText);

        // Zero additional network calls
        Assert.Equal(callsAfterFirst, networkCallCount);
    }

    // =================================================================
    // 8. OFFLINE MODE (LOCAL LRC SERVED WITH 0 NETWORK CALLS)
    // =================================================================

    [Fact]
    public async Task CompositeLyricsService_LocalLrcFile_ServedCompletelyOffline()
    {
        int networkCallCount = 0;
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Interlocked.Increment(ref networkCallCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var provider = new LrcLibLyricsProvider(httpService, rateRegistry);
        var orchestrator = new OnlineLyricsOrchestrator(new[] { provider }, twoTierCache);
        var localLyricsService = new LyricsService();
        var compositeService = new CompositeLyricsService(localLyricsService, orchestrator);

        // Create local audio and .lrc sidecar file
        string audioFile = Path.Combine(_tempDir, "song.mp3");
        string lrcFile = Path.Combine(_tempDir, "song.lrc");

        File.WriteAllText(audioFile, "dummy audio");
        File.WriteAllText(lrcFile, "[00:02.00] Local Line 1\n[00:06.00] Local Line 2");

        var track = new Track("tr_offline", "Local Song", "ar1", "Local Artist", "al1", "Local Album", 120, audioFile, "Local", 1, 2024, DateTime.UtcNow);

        var result = await compositeService.GetLyricsAsync(track);

        Assert.Equal(LyricsState.Synced, result.State);
        Assert.NotNull(result.SyncedLines);
        Assert.Equal(2, result.SyncedLines.Count);
        Assert.Equal("Local Line 1", result.SyncedLines[0].Text);

        // 0 network requests dispatched
        Assert.Equal(0, networkCallCount);
    }
}
