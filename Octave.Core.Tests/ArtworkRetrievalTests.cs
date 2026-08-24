using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Cache;
using Octave.Core.Services.Database;
using Octave.Core.Services.External;
using Octave.Core.Services.External.Artwork;
using Octave.Core.Services.Metadata;
using Octave.Core.Services.Network;
using Xunit;

namespace Octave.Core.Tests;

public class ArtworkRetrievalTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDbContext _dbContext;
    private static readonly byte[] ValidJpegBytes = new byte[] {
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x01, 0x00, 0x60, 0x00, 0x60, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43,
        0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08, 0x07, 0x07, 0x07, 0x09,
        0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12,
        0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20,
        0x24, 0x2E, 0x27, 0x20, 0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29,
        0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27, 0x39, 0x3D, 0x38, 0x32,
        0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01,
        0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xC4, 0x00, 0x1F, 0x00, 0x00,
        0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F,
        0x00, 0xBF, 0x00, 0xFF, 0xD9
    };

    public ArtworkRetrievalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_ArtTests_" + Guid.NewGuid().ToString("N"));
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
        // TEST-09: lets cancellation tests prove the handler was NEVER invoked,
        // rather than merely that some empty result came back.
        public int CallCount;

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> HandlerFunc { get; set; } =
            (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            return HandlerFunc(request, cancellationToken);
        }
    }

    // =================================================================
    // 1. SUCCESSFUL ARTWORK RETRIEVAL
    // =================================================================

    [Fact]
    public async Task ResolveAndCacheAlbumArtworkAsync_SuccessfulRetrieval_ReturnsValidLocalToken()
    {
        string caaJson = @"
        {
            ""images"": [
                {
                    ""front"": true,
                    ""image"": ""https://images.example.com/art/12345.jpg"",
                    ""thumbnails"": {
                        ""500"": ""https://images.example.com/art/12345-500.jpg""
                    }
                }
            ]
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(ValidJpegBytes)
                    };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    return Task.FromResult(response);
                }
                else if (url.Contains("/release/rel-123"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(caaJson)
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var artworkCacheManager = new ArtworkCacheManager(_tempDir);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var caaProvider = new CoverArtArchiveArtworkProvider(httpService, null, rateRegistry);
        var orchestrator = new ExternalArtworkOrchestrator(
            new[] { caaProvider },
            Array.Empty<IExternalArtistImageProvider>(),
            httpService,
            artworkCacheManager,
            twoTierCache);

        var extIds = new ExternalIds("rel-123", AdditionalIds: new Dictionary<string, string> { ["MusicBrainzReleaseId"] = "rel-123" });
        var token = await orchestrator.ResolveAndCacheAlbumArtworkAsync("A Night at the Opera", "Queen", extIds);

        Assert.NotNull(token);
        Assert.StartsWith("ArtworkCache/", token);
        Assert.EndsWith(".jpg", token);

        // Verify the file was saved to the cache directory and is valid on disk
        string relativeFile = token.Replace("ArtworkCache/", "");
        string absolutePath = Path.Combine(_tempDir, relativeFile);
        Assert.True(File.Exists(absolutePath));
        Assert.True(new FileInfo(absolutePath).Length >= 8);
    }

    // =================================================================
    // 2. NO ARTWORK AVAILABLE (404 NOT FOUND)
    // =================================================================

    [Fact]
    public async Task ResolveAndCacheAlbumArtworkAsync_NoArtwork_ReturnsNullGracefully()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var artworkCacheManager = new ArtworkCacheManager(_tempDir);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var caaProvider = new CoverArtArchiveArtworkProvider(httpService, null, rateRegistry);
        var orchestrator = new ExternalArtworkOrchestrator(
            new[] { caaProvider },
            Array.Empty<IExternalArtistImageProvider>(),
            httpService,
            artworkCacheManager,
            twoTierCache);

        var extIds = new ExternalIds("rel-missing-999");
        var token = await orchestrator.ResolveAndCacheAlbumArtworkAsync("Unknown Album", "Unknown Artist", extIds);

        Assert.Null(token);
    }

    // =================================================================
    // 3. INVALID IMAGE (CORRUPTED / HTML ERROR RESPONSE)
    // =================================================================

    [Fact]
    public async Task ResolveAndCacheAlbumArtworkAsync_InvalidImageBytes_RejectsAndDoesNotCache()
    {
        string caaJson = @"
        {
            ""images"": [
                {
                    ""front"": true,
                    ""image"": ""https://images.example.com/art/bad.jpg""
                }
            ]
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.Contains("/release/rel-bad"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(caaJson)
                    });
                }
                else
                {
                    // Returns HTML error page instead of valid image binary
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<html><body>500 Internal Server Error</body></html>")
                    });
                }
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var artworkCacheManager = new ArtworkCacheManager(_tempDir);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var caaProvider = new CoverArtArchiveArtworkProvider(httpService, null, rateRegistry);
        var orchestrator = new ExternalArtworkOrchestrator(
            new[] { caaProvider },
            Array.Empty<IExternalArtistImageProvider>(),
            httpService,
            artworkCacheManager,
            twoTierCache);

        var extIds = new ExternalIds("rel-bad");
        var token = await orchestrator.ResolveAndCacheAlbumArtworkAsync("Bad Album", "Artist", extIds);

        // Validation rejects HTML bytes
        Assert.Null(token);
    }

    // =================================================================
    // 4. NETWORK FAILURE (500 ERROR / TIMEOUT)
    // =================================================================

    [Fact]
    public async Task ResolveAndCacheAlbumArtworkAsync_NetworkFailure_ReturnsNullWithoutCrashing()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var artworkCacheManager = new ArtworkCacheManager(_tempDir);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var caaProvider = new CoverArtArchiveArtworkProvider(httpService, null, rateRegistry);
        var orchestrator = new ExternalArtworkOrchestrator(
            new[] { caaProvider },
            Array.Empty<IExternalArtistImageProvider>(),
            httpService,
            artworkCacheManager,
            twoTierCache);

        var token = await orchestrator.ResolveAndCacheAlbumArtworkAsync("Album", "Artist");

        Assert.Null(token);
    }

    // =================================================================
    // 5. DUPLICATE CONCURRENT REQUESTS (IN-FLIGHT DEDUPLICATION)
    // =================================================================

    [Fact]
    public async Task ResolveAndCacheAlbumArtworkAsync_ConcurrentDuplicateRequests_DeduplicatesToSingleDownload()
    {
        int networkDownloadCount = 0;
        string caaJson = @"
        {
            ""images"": [
                {
                    ""front"": true,
                    ""image"": ""https://images.example.com/art/dedup.jpg""
                }
            ]
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = async (req, ct) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref networkDownloadCount);
                    await Task.Delay(100, ct); // simulate image download latency
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(ValidJpegBytes)
                    };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    return response;
                }
                else if (url.Contains("/release/rel-dedup"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(caaJson)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var artworkCacheManager = new ArtworkCacheManager(_tempDir);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var caaProvider = new CoverArtArchiveArtworkProvider(httpService, null, rateRegistry);
        var orchestrator = new ExternalArtworkOrchestrator(
            new[] { caaProvider },
            Array.Empty<IExternalArtistImageProvider>(),
            httpService,
            artworkCacheManager,
            twoTierCache);

        var extIds = new ExternalIds("rel-dedup");

        // Fire 4 concurrent resolution requests for the same album
        var t1 = orchestrator.ResolveAndCacheAlbumArtworkAsync("Dedup Album", "Dedup Artist", extIds);
        var t2 = orchestrator.ResolveAndCacheAlbumArtworkAsync("Dedup Album", "Dedup Artist", extIds);
        var t3 = orchestrator.ResolveAndCacheAlbumArtworkAsync("Dedup Album", "Dedup Artist", extIds);
        var t4 = orchestrator.ResolveAndCacheAlbumArtworkAsync("Dedup Album", "Dedup Artist", extIds);

        var tokens = await Task.WhenAll(t1, t2, t3, t4);

        foreach (var t in tokens)
        {
            Assert.NotNull(t);
            Assert.StartsWith("ArtworkCache/", t);
        }

        // Only 1 image download was dispatched
        Assert.Equal(1, networkDownloadCount);
    }

    // =================================================================
    // 6. CACHE HIT
    // =================================================================

    [Fact]
    public async Task ResolveAndCacheAlbumArtworkAsync_SecondCall_ReturnsCachedTokenWithoutNetwork()
    {
        int networkCallCount = 0;
        string caaJson = @"{ ""images"": [ { ""front"": true, ""image"": ""https://images.example.com/art/hit.jpg"" } ] }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Interlocked.Increment(ref networkCallCount);
                string url = req.RequestUri!.ToString();
                if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(ValidJpegBytes)
                    };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    return Task.FromResult(response);
                }
                else if (url.Contains("/release/rel-hit"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(caaJson)
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var artworkCacheManager = new ArtworkCacheManager(_tempDir);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var caaProvider = new CoverArtArchiveArtworkProvider(httpService, null, rateRegistry);
        var orchestrator = new ExternalArtworkOrchestrator(
            new[] { caaProvider },
            Array.Empty<IExternalArtistImageProvider>(),
            httpService,
            artworkCacheManager,
            twoTierCache);

        var extIds = new ExternalIds("rel-hit");

        // First call: populates cache
        var token1 = await orchestrator.ResolveAndCacheAlbumArtworkAsync("Cache Album", "Cache Artist", extIds);
        Assert.NotNull(token1);
        int callsAfterFirst = networkCallCount;

        // Second call: must hit cache
        var token2 = await orchestrator.ResolveAndCacheAlbumArtworkAsync("Cache Album", "Cache Artist", extIds);

        Assert.Equal(token1, token2);
        Assert.Equal(callsAfterFirst, networkCallCount); // No new network requests
    }

    // =================================================================
    // 7. CANCELLATION
    // =================================================================

    [Fact]
    public async Task ResolveAndCacheAlbumArtworkAsync_CancelledToken_ReturnsNullOrExitsCleanly()
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
        var artworkCacheManager = new ArtworkCacheManager(_tempDir);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var caaProvider = new CoverArtArchiveArtworkProvider(httpService, null, rateRegistry);
        var orchestrator = new ExternalArtworkOrchestrator(
            new[] { caaProvider },
            Array.Empty<IExternalArtistImageProvider>(),
            httpService,
            artworkCacheManager,
            twoTierCache);

        var token = await orchestrator.ResolveAndCacheAlbumArtworkAsync("Album", "Artist", ct: cts.Token);

        // TEST-09: a null return alone proves nothing — the request could have
        // gone out and failed. The pre-cancelled token must short-circuit BEFORE
        // the handler is touched at all.
        Assert.Equal(0, mockHandler.CallCount);
        Assert.Null(token);
    }

    // =================================================================
    // 8. OFFLINE OPERATION FOR LOCAL / CACHED ARTWORK
    // =================================================================

    [Fact]
    public async Task ArtworkCacheManager_ServesExistingLocalFile_CompletelyOffline()
    {
        var artworkCacheManager = new ArtworkCacheManager(_tempDir);

        // Simulate an embedded track artwork byte caching during local file scanning
        string? token = await artworkCacheManager.CacheBytesAsync(ValidJpegBytes, "image/jpeg");

        Assert.NotNull(token);
        Assert.StartsWith("ArtworkCache/", token);

        // Verify the file exists locally and can be read with 0 network dependencies
        string localRelative = token.Replace("ArtworkCache/", "");
        string localAbsolute = Path.Combine(_tempDir, localRelative);

        Assert.True(File.Exists(localAbsolute));
        byte[] readBackBytes = await File.ReadAllBytesAsync(localAbsolute);
        Assert.Equal(ValidJpegBytes.Length, readBackBytes.Length);
        Assert.Equal(ValidJpegBytes[0], readBackBytes[0]);
    }

    // =================================================================
    // 9. BATCH 7 — ENTITY-AWARE MBID ROUTING (CAA-01) & NO SPECULATIVE URLS (CAA-02)
    // =================================================================

    private static CoverArtArchiveArtworkProvider BuildDirectCaaProvider(HttpMessageHandler handler, ProviderRateLimiterRegistry rateRegistry)
    {
        var httpClient = new HttpClient(handler);
        var httpService = new HttpService(rateRegistry, httpClient);
        return new CoverArtArchiveArtworkProvider(httpService, null, rateRegistry);
    }

    [Fact]
    public async Task CaaProvider_RecordingStampedMbid_NeverQueriedAsRelease()
    {
        int requestCount = 0;
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Interlocked.Increment(ref requestCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };
        var provider = BuildDirectCaaProvider(mockHandler, new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10)));

        // Track candidates carry a RECORDING mbid in MusicBrainzId (stamped by
        // the MusicBrainz provider). The old code fed it to /release/{mbid}.
        var extIds = new ExternalIds("rec-ambiguous",
            AdditionalIds: new Dictionary<string, string> { [ExternalIdKinds.EntityKind] = ExternalIdKinds.KindRecording });

        var urls = await provider.SearchAlbumArtworkUrlsAsync("Whatever Album", "Whatever Artist", extIds);

        Assert.Empty(urls);
        Assert.Equal(0, requestCount); // no /release/{recording} fetch may fire
    }

    [Fact]
    public async Task CaaProvider_ReleaseStampedMbid_IsUsedForReleaseEndpoint()
    {
        int requestCount = 0;
        string caaJson = @"{ ""images"": [ { ""front"": true, ""image"": ""https://images.example.com/art/stamped.jpg"",
            ""thumbnails"": { ""500"": ""https://images.example.com/art/stamped-500.jpg"" } } ] }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Interlocked.Increment(ref requestCount);
                string url = req.RequestUri!.ToString();
                if (url.Contains("/release/rel-stamped"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(caaJson)
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };
        var provider = BuildDirectCaaProvider(mockHandler, new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10)));

        // Positive control: a RELEASE-stamped mbid still routes to the release endpoint.
        // NF-31: with no 1200px thumbnail offered, the full-resolution "image"
        // URL is now preferred over the 500px thumbnail.
        var extIds = new ExternalIds("rel-stamped",
            AdditionalIds: new Dictionary<string, string> { [ExternalIdKinds.EntityKind] = ExternalIdKinds.KindRelease });

        var urls = await provider.SearchAlbumArtworkUrlsAsync("Some Album", "Some Artist", extIds);

        Assert.Single(urls);
        Assert.Contains("stamped.jpg", urls[0]);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task CaaProvider_ReleaseJsonMissing_YieldsNoFabricatedFrontUrl()
    {
        int requestCount = 0;
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Interlocked.Increment(ref requestCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };
        var provider = BuildDirectCaaProvider(mockHandler, new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10)));

        var extIds = new ExternalIds("rel-missing",
            AdditionalIds: new Dictionary<string, string> { [ExternalIdKinds.EntityKind] = ExternalIdKinds.KindRelease });

        var urls = await provider.SearchAlbumArtworkUrlsAsync("Missing Album", "Missing Artist", extIds);

        // CAA-02: a JSON-fetch failure is not evidence art exists — the old code
        // fabricated "…/front" and handed downstream a URL that could only 404.
        Assert.Empty(urls);
        Assert.DoesNotContain(urls, u => u.Contains("/front"));
        Assert.Equal(1, requestCount); // exactly one attempt; nothing speculative follows
    }
}
