using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;
using Octave.Core.Services.Cache;
using Octave.Core.Services.Database;
using Octave.Core.Services.External.Artist;
using Octave.Core.Services.Metadata;
using Octave.Core.Services.Network;
using Xunit;

namespace Octave.Core.Tests;

public class ArtistEnrichmentTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDbContext _dbContext;

    private static readonly byte[] ValidJpegBytes = new byte[] {
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x01, 0x00, 0x60, 0x00, 0x60, 0x00, 0x00, 0xFF, 0xD9
    };

    public ArtistEnrichmentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_ArtistEnrichTests_" + Guid.NewGuid().ToString("N"));
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
    // 1. ARTIST LOOKUP BY EXTERNAL ID (MusicBrainz Artist ID)
    // =================================================================

    [Fact]
    public async Task GetEnrichedArtistAsync_ByMbid_ReturnsRichProfile()
    {
        string tadbJson = @"
        {
            ""artists"": [
                {
                    ""idArtist"": ""111493"",
                    ""strArtist"": ""Queen"",
                    ""strBiographyEN"": ""Queen are a British rock band formed in London in 1970."",
                    ""strGenre"": ""Rock"",
                    ""strCountry"": ""United Kingdom"",
                    ""intBornYear"": ""1970"",
                    ""strWebsite"": ""http://www.queenonline.com"",
                    ""strTwitter"": ""QueenWillRock"",
                    ""strMusicBrainzID"": ""0383dadf-2a4e-4d10-a4f8-e9e809d38c27"",
                    ""strArtistThumb"": ""https://images.example.com/artists/queen_thumb.jpg""
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
                else if (url.Contains("artist-mb.php?i=0383dadf"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(tadbJson)
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var artworkCache = new ArtworkCacheManager(_tempDir);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var provider = new TheAudioDbArtistEnrichmentProvider(httpService, null, rateRegistry);
        var service = new ArtistEnrichmentService(new[] { provider }, httpService, artworkCache, twoTierCache);

        var profile = await service.GetEnrichedArtistAsync("Queen", "0383dadf-2a4e-4d10-a4f8-e9e809d38c27");

        Assert.NotNull(profile);
        Assert.Equal("Queen", profile.Name);
        Assert.Equal("0383dadf-2a4e-4d10-a4f8-e9e809d38c27", profile.ArtistId);
        Assert.Contains("British rock band", profile.Biography);
        Assert.Equal("Rock", profile.PrimaryGenre);
        Assert.Equal("United Kingdom", profile.Country);
        Assert.Equal(1970, profile.FormedYear);
        Assert.NotNull(profile.ExternalLinks);
        Assert.Equal("http://www.queenonline.com", profile.ExternalLinks["Website"]);
        Assert.NotNull(profile.LocalImageToken);
        Assert.StartsWith("ArtworkCache/", profile.LocalImageToken);

        // Verify local image file exists on disk
        string relativeFile = profile.LocalImageToken.Replace("ArtworkCache/", "");
        string absolutePath = Path.Combine(_tempDir, relativeFile);
        Assert.True(File.Exists(absolutePath));
    }

    // =================================================================
    // 2. NO RESULT (Graceful Null)
    // =================================================================

    [Fact]
    public async Task GetEnrichedArtistAsync_NoResult_ReturnsNullGracefully()
    {
        string emptyJson = @"{ ""artists"": null }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(emptyJson)
            })
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var artworkCache = new ArtworkCacheManager(_tempDir);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var provider = new TheAudioDbArtistEnrichmentProvider(httpService, null, rateRegistry);
        var service = new ArtistEnrichmentService(new[] { provider }, httpService, artworkCache, twoTierCache);

        var profile = await service.GetEnrichedArtistAsync("NonexistentArtistName123");

        Assert.Null(profile);
    }

    // =================================================================
    // 3. PROVIDER FAILURE (500 Error / Network Error)
    // =================================================================

    [Fact]
    public async Task GetEnrichedArtistAsync_ProviderFailure_ReturnsNullWithoutCrashing()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var artworkCache = new ArtworkCacheManager(_tempDir);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var provider = new TheAudioDbArtistEnrichmentProvider(httpService, null, rateRegistry);
        var service = new ArtistEnrichmentService(new[] { provider }, httpService, artworkCache, twoTierCache);

        var profile = await service.GetEnrichedArtistAsync("Queen", "mb-art-1");

        Assert.Null(profile);
    }

    // =================================================================
    // 4. IMAGE RETRIEVAL & MAGIC BYTE VALIDATION
    // =================================================================

    [Fact]
    public async Task GetEnrichedArtistAsync_InvalidImageBytes_RejectsAndOmitsLocalImageToken()
    {
        string tadbJson = @"
        {
            ""artists"": [
                {
                    ""idArtist"": ""111493"",
                    ""strArtist"": ""Queen"",
                    ""strBiographyEN"": ""Bio..."",
                    ""strArtistThumb"": ""https://images.example.com/artists/bad_image.jpg""
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
                    // Return HTML error page instead of image binary
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<html><body>500 Internal Server Error</body></html>")
                    });
                }
                else
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(tadbJson)
                    });
                }
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var artworkCache = new ArtworkCacheManager(_tempDir);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var provider = new TheAudioDbArtistEnrichmentProvider(httpService, null, rateRegistry);
        var service = new ArtistEnrichmentService(new[] { provider }, httpService, artworkCache, twoTierCache);

        var profile = await service.GetEnrichedArtistAsync("Queen", "mb-art-1");

        Assert.NotNull(profile);
        Assert.Equal("Queen", profile.Name);
        // Image validation rejects HTML page: LocalImageToken must be null
        Assert.Null(profile.LocalImageToken);
    }

    // =================================================================
    // 5. REPEATED REQUESTS USING CACHE (Zero Additional Network Calls)
    // =================================================================

    [Fact]
    public async Task GetEnrichedArtistAsync_SecondCall_ReturnsCachedProfileWithoutNetwork()
    {
        int networkCallCount = 0;
        string tadbJson = @"
        {
            ""artists"": [
                {
                    ""idArtist"": ""111493"",
                    ""strArtist"": ""Queen"",
                    ""strBiographyEN"": ""Cached Queen Biography"",
                    ""strArtistThumb"": ""https://images.example.com/artists/queen_cache.jpg""
                }
            ]
        }";

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
                else
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(tadbJson)
                    });
                }
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var artworkCache = new ArtworkCacheManager(_tempDir);
        var twoTierCache = new TwoTierExternalDataCache(_dbContext);

        var provider = new TheAudioDbArtistEnrichmentProvider(httpService, null, rateRegistry);
        var service = new ArtistEnrichmentService(new[] { provider }, httpService, artworkCache, twoTierCache);

        // First call: fetches from provider & image CDN
        var p1 = await service.GetEnrichedArtistAsync("Queen", "mb-cache-99");
        Assert.NotNull(p1);
        int callsAfterFirst = networkCallCount;

        // Second call: must hit Two-Tier cache
        var p2 = await service.GetEnrichedArtistAsync("Queen", "mb-cache-99");
        Assert.NotNull(p2);
        Assert.Equal(p1.Biography, p2.Biography);
        Assert.Equal(p1.LocalImageToken, p2.LocalImageToken);

        // Zero additional network calls
        Assert.Equal(callsAfterFirst, networkCallCount);
    }

    // =================================================================
    // 6. BATCH 7 — API KEY RESOLUTION (ADB-01) & MULTI-RESULT PICKING (ADB-02)
    // =================================================================

    private sealed class FakeExternalDataSettingsService : Octave.Core.Services.External.Settings.IExternalDataSettingsService
    {
        public event EventHandler<ExternalDataSettings>? SettingsChanged { add { } remove { } }
        public ExternalDataSettings CurrentSettings { get; set; } = new();
        public Task LoadSettingsAsync() => Task.CompletedTask;
        public Task UpdateSettingsAsync(ExternalDataSettings settings)
        {
            CurrentSettings = settings;
            return Task.CompletedTask;
        }
        public Task<TestConnectionResult> TestTheAudioDbConnectionAsync(string apiKey, CancellationToken ct = default) =>
            Task.FromResult(new TestConnectionResult(true, "fake"));
    }

    private static readonly string SingleArtistJson = @"
    {
        ""artists"": [
            {
                ""idArtist"": ""1"",
                ""strArtist"": ""Key Check Artist"",
                ""strMusicBrainzID"": ""mbid-target""
            }
        ]
    }";

    [Fact]
    public async Task TadbProvider_PaddedSettingsKey_IsTrimmedAndPreferredOverOptionsKey()
    {
        string? capturedUrl = null;
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.Contains("artist-mb.php"))
                {
                    capturedUrl = url;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SingleArtistJson)
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);

        // ADB-01: the old `?.Trim() ?? _options.ApiKey` chain treated a blank
        // settings key as PRESENT (Trim → ""), building "{base}//artist-mb.php".
        // The resolution now treats blank exactly like absent, and trims padding.
        var settings = new FakeExternalDataSettingsService
        {
            CurrentSettings = new ExternalDataSettings { TheAudioDbApiKey = "  settings-key-42  " }
        };
        var options = new TheAudioDbOptions { ApiKey = "options-fallback-key" };

        var provider = new TheAudioDbArtistEnrichmentProvider(httpService, options, rateRegistry, settings);

        var profile = await provider.GetArtistProfileByMbidAsync("mbid-target");

        Assert.NotNull(profile);
        Assert.NotNull(capturedUrl);
        Assert.Contains("/settings-key-42/artist-mb.php", capturedUrl);
        Assert.DoesNotContain("options-fallback-key", capturedUrl);
        Assert.DoesNotContain("/api/v1/json//", capturedUrl); // the malformed double-slash shape
    }

    [Fact]
    public async Task TadbProvider_NameSearchMultipleArtists_PicksExactNormalizedName()
    {
        string multiJson = @"
        {
            ""artists"": [
                { ""idArtist"": ""10"", ""strArtist"": ""Queensryche"", ""strMusicBrainzID"": ""mbid-qr"" },
                { ""idArtist"": ""11"", ""strArtist"": ""QUEEN!"", ""strMusicBrainzID"": ""mbid-real"" }
            ]
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(multiJson)
            })
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);

        var provider = new TheAudioDbArtistEnrichmentProvider(httpService, null, rateRegistry);

        // ADB-02: search.php returns several artists sharing a name — binding
        // Artists[0] attached the WRONG artist's bio/images ("Queensryche" here).
        var profile = await provider.GetArtistProfileByNameAsync("queen!");

        Assert.NotNull(profile);
        Assert.Equal("QUEEN!", profile.Name);
        Assert.Equal("mbid-real", profile.ExternalIds.MusicBrainzId);
    }

    [Fact]
    public async Task TadbProvider_MbidSearchMultipleArtists_PicksMatchingMbidRow()
    {
        string multiJson = @"
        {
            ""artists"": [
                { ""idArtist"": ""20"", ""strArtist"": ""Decoy Artist"", ""strMusicBrainzID"": ""mbid-decoy"" },
                { ""idArtist"": ""21"", ""strArtist"": ""True Artist"", ""strMusicBrainzID"": ""mbid-true"" }
            ]
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(multiJson)
            })
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);

        var provider = new TheAudioDbArtistEnrichmentProvider(httpService, null, rateRegistry);

        var profile = await provider.GetArtistProfileByMbidAsync("mbid-true");

        Assert.NotNull(profile);
        Assert.Equal("True Artist", profile.Name);
    }
}
