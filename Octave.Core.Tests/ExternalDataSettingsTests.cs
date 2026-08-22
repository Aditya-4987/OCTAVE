using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;
using Octave.Core.Services.Database;
using Octave.Core.Services.External.Artist;
using Octave.Core.Services.External.Artwork;
using Octave.Core.Services.External.Lyrics;
using Octave.Core.Services.External.MusicBrainz;
using Octave.Core.Services.External.Settings;
using Octave.Core.Services.Network;
using Xunit;

namespace Octave.Core.Tests;

public class ExternalDataSettingsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDbContext _dbContext;

    public ExternalDataSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_SettingsTests_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void DefaultSettings_HaveSafeAndExpectedDefaults()
    {
        var settings = new ExternalDataSettings();

        Assert.True(settings.EnableOnlineMetadata);
        Assert.True(settings.EnableOnlineLyrics);
        Assert.True(settings.EnableOnlineArtwork);
        Assert.True(settings.EnableArtistEnrichment);
        Assert.False(settings.OfflineOnlyMode);

        Assert.True(settings.MusicBrainzEnabled);
        Assert.True(settings.LrcLibEnabled);
        Assert.True(settings.CoverArtArchiveEnabled);
        Assert.True(settings.TheAudioDbEnabled);

        Assert.Equal("2", settings.TheAudioDbApiKey);
        Assert.Equal(MetadataWritePolicy.WriteOnlyWhenMissingAndHighConfidence, settings.WritePolicy);

        Assert.False(settings.AutoFillMissingMetadata);
        Assert.False(settings.ReplaceExistingMetadata);
        Assert.False(settings.ReplaceExistingArtwork);
        Assert.True(settings.WriteExternalIdsToTags);
    }

    [Fact]
    public async Task SettingsService_SavesAndLoadsSettingsFromDatabase()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var tadbOptions = new TheAudioDbOptions();

        var service = new ExternalDataSettingsService(_dbContext, httpService, tadbOptions);
        await service.LoadSettingsAsync();

        var customSettings = new ExternalDataSettings
        {
            EnableOnlineLyrics = false,
            TheAudioDbApiKey = "custom_test_key_123",
            WritePolicy = MetadataWritePolicy.AlwaysPreferOnline,
            OfflineOnlyMode = true,
            CacheRetentionDays = 90,
            MaxConcurrentRequests = 8
        };

        await service.UpdateSettingsAsync(customSettings);

        // Create a second service instance to verify SQLite disk persistence
        var service2 = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await service2.LoadSettingsAsync();

        Assert.False(service2.CurrentSettings.EnableOnlineLyrics);
        Assert.Equal("custom_test_key_123", service2.CurrentSettings.TheAudioDbApiKey);
        Assert.Equal(MetadataWritePolicy.AlwaysPreferOnline, service2.CurrentSettings.WritePolicy);
        Assert.True(service2.CurrentSettings.OfflineOnlyMode);
        Assert.Equal(90, service2.CurrentSettings.CacheRetentionDays);
        Assert.Equal(8, service2.CurrentSettings.MaxConcurrentRequests);
    }

    [Fact]
    public async Task MusicBrainz_WhenDisabledInSettings_DoesNotMakeNetworkCalls()
    {
        int networkCalls = 0;
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Interlocked.Increment(ref networkCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"recordings\":[]}")
                });
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var provider = new MusicBrainzMetadataProvider(httpService, settingsService: settingsService);
        Assert.True(provider.IsEnabled);

        // Disable MusicBrainz
        var settings = settingsService.CurrentSettings;
        settings.MusicBrainzEnabled = false;
        await settingsService.UpdateSettingsAsync(settings);

        Assert.False(provider.IsEnabled);

        var candidates = await provider.SearchTrackCandidatesAsync("Fix You", "Coldplay");
        Assert.Empty(candidates);
        Assert.Equal(0, networkCalls);
    }

    [Fact]
    public async Task LrcLib_WhenLyricsDisabled_DoesNotMakeNetworkCalls()
    {
        int networkCalls = 0;
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Interlocked.Increment(ref networkCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"plainLyrics\":\"test\"}")
                });
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var provider = new LrcLibLyricsProvider(httpService, settingsService: settingsService);
        Assert.True(provider.IsEnabled);

        // Turn off global lyrics toggle
        var settings = settingsService.CurrentSettings;
        settings.EnableOnlineLyrics = false;
        await settingsService.UpdateSettingsAsync(settings);

        Assert.False(provider.IsEnabled);

        var result = await provider.FetchLyricsAsync("Fix You", "Coldplay");
        Assert.Null(result);
        Assert.Equal(0, networkCalls);
    }

    [Fact]
    public async Task CoverArtArchive_WhenOfflineOnly_DoesNotMakeNetworkCalls()
    {
        int networkCalls = 0;
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Interlocked.Increment(ref networkCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"images\":[]}")
                });
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var provider = new CoverArtArchiveArtworkProvider(httpService, settingsService: settingsService);
        Assert.True(provider.IsEnabled);

        // Turn on offline only mode
        var settings = settingsService.CurrentSettings;
        settings.OfflineOnlyMode = true;
        await settingsService.UpdateSettingsAsync(settings);

        Assert.False(provider.IsEnabled);

        var result = await provider.SearchAlbumArtworkUrlsAsync("Parachutes", "Coldplay");
        Assert.Empty(result);
        Assert.Equal(0, networkCalls);
    }

    [Fact]
    public async Task TheAudioDb_UsesConfiguredApiKeyInRequests()
    {
        string? requestedUrl = null;
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                requestedUrl = req.RequestUri?.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"artists\":[{\"strArtist\":\"Coldplay\"}]}")
                });
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var tadbOptions = new TheAudioDbOptions();
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, tadbOptions);
        await settingsService.LoadSettingsAsync();

        var provider = new TheAudioDbArtistEnrichmentProvider(httpService, tadbOptions, settingsService: settingsService);

        // Update with custom personal API key
        var settings = settingsService.CurrentSettings;
        settings.TheAudioDbApiKey = "my_custom_patreon_key_999";
        await settingsService.UpdateSettingsAsync(settings);

        var profile = await provider.GetArtistProfileByNameAsync("Coldplay");

        Assert.NotNull(profile);
        Assert.NotNull(requestedUrl);
        Assert.Contains("my_custom_patreon_key_999", requestedUrl);
    }

    [Fact]
    public async Task TheAudioDb_EmptyApiKey_DisablesProvider()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        await settingsService.LoadSettingsAsync();

        var provider = new TheAudioDbArtistEnrichmentProvider(httpService, settingsService: settingsService);
        Assert.True(provider.IsEnabled);

        var settings = settingsService.CurrentSettings;
        settings.TheAudioDbApiKey = "   ";
        await settingsService.UpdateSettingsAsync(settings);

        Assert.False(provider.IsEnabled);
    }

    [Fact]
    public async Task TestConnection_Success_ReturnsProperStatus()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"artists\":[{\"strArtist\":\"Coldplay\"}]}")
            })
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());

        var result = await settingsService.TestTheAudioDbConnectionAsync("2");

        Assert.True(result.Success);
        Assert.Contains("Successfully connected", result.Message);
    }

    [Fact]
    public async Task TestConnection_Security_NeverExposesSecretInErrorMessage()
    {
        const string secretKey = "super_secret_user_key_987654321";
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"Invalid API key\"}")
            })
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var settingsService = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());

        var result = await settingsService.TestTheAudioDbConnectionAsync(secretKey);

        Assert.False(result.Success);
        Assert.DoesNotContain(secretKey, result.Message);
    }
}
