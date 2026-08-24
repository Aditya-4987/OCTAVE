using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Moq;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Cache;
using Octave.Core.Services.Database;
using Octave.Core.Services.External;
using Octave.Core.Services.External.Settings;
using Octave.Core.Services.Metadata;
using Octave.Core.Services.Network;
using Xunit;

namespace Octave.Core.Tests;

/// <summary>
/// Acceptance tests for Batch 10 cache/orchestrator hardening
/// (CODEBASE_AUDIT §15): CACHE-02 defensive copies, CACHE-03 LRU budget,
/// CACHE-05 corrupt-row purge, CACHE-06 cancellation-before-write,
/// ORC-01 negative-result caching, OL-06 cross-provider candidate dedup, and
/// ORC-03/OL-08 centralized token resolution with memoized existence probes.
/// </summary>
public class CacheResilienceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDbContext _dbContext;

    // Proven-valid JPEG accepted by ImageValidator (same bytes as ArtworkRetrievalTests).
    private static readonly byte[] ValidJpegBytes = new byte[] {
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x01, 0x00, 0x60, 0x00, 0x60, 0x00, 0x00, 0xFF, 0xD9
    };

    public CacheResilienceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_CacheRes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "cache.db");
        _dbContext = new SqliteDbContext(_dbPath);
        _dbContext.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    // =================================================================
    // CACHE-06: a cancelled SetAsync must not leave an L1 entry behind.
    // =================================================================

    [Fact]
    public async Task SetAsync_CancelledToken_DoesNotPopulateAnyTier()
    {
        var cache = new TwoTierExternalDataCache(_dbContext);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await cache.SetAsync("cancelled_key", new[] { "value" }, ct: cancelled.Token);

        // Pre-CACHE-06 the L1 write preceded the ct check: this read would have
        // returned the value from memory even though the caller cancelled.
        Assert.Null(await cache.GetAsync<string[]>("cancelled_key"));
    }

    // =================================================================
    // CACHE-02: mutable collection payloads are copied on the way in and out.
    // =================================================================

    [Fact]
    public async Task ListPayload_MutationsByOneConsumer_DoNotLeakToOthers()
    {
        var cache = new TwoTierExternalDataCache(_dbContext);
        var original = new List<string> { "alpha", "beta" };
        await cache.SetAsync("list_key", original);

        // Mutating the CALLER's reference after the set must not touch the master.
        original.Add("gamma");

        var firstRead = await cache.GetAsync<List<string>>("list_key");
        Assert.NotNull(firstRead);
        Assert.Equal(2, firstRead!.Count);

        // Mutating a handed-out copy must not poison subsequent readers.
        firstRead.Add("delta");
        firstRead.RemoveAt(0);

        var secondRead = await cache.GetAsync<List<string>>("list_key");
        Assert.NotNull(secondRead);
        Assert.Equal(new[] { "alpha", "beta" }, secondRead);
    }

    // =================================================================
    // CACHE-03: eviction keeps a true-LRU budget and retains hot entries.
    // =================================================================

    [Fact]
    public async Task LruEviction_KeepsBudget_AndRetainsRecentlyUsedEntries()
    {
        var cache = new TwoTierExternalDataCache(_dbContext, maxL1Capacity: 5);

        for (int i = 0; i < 5; i++)
        {
            await cache.SetAsync($"k{i}", new[] { $"v{i}" });
        }

        // Let the last-access clock advance well past the bulk inserts (the tick
        // source is ~15ms granular, so stay several ticks clear)...
        await Task.Delay(60);
        // ...then touch k0 so it is unambiguously the most recently used.
        Assert.NotNull(await cache.GetAsync<string[]>("k0"));
        await Task.Delay(60);

        // Crossing the capacity boundary purges a proportional batch; k0 must
        // survive because its last access is newer than every untouched entry.
        await cache.SetAsync("k5", new[] { "v5" });

        // Budget is asserted through the internal residency hooks: GetAsync
        // cannot observe eviction because keys purged from L1 fall back to the
        // L2 tier by design (and get promoted straight back).
        Assert.Equal(5, cache.L1EntryCount);

        // Retention: the touched entry and the fresh insert are both resident,
        // and exactly one of the four untouched middle entries was evicted.
        Assert.True(cache.IsL1Resident("k0"));
        Assert.True(cache.IsL1Resident("k5"));
        Assert.Equal(1, new[] { "k1", "k2", "k3", "k4" }.Count(k => !cache.IsL1Resident(k)));

        // Cross-tier readability is unaffected either way.
        for (int i = 0; i <= 5; i++)
        {
            Assert.Equal(new[] { $"v{i}" }, await cache.GetAsync<string[]>($"k{i}"));
        }
    }

    // =================================================================
    // CACHE-05: a corrupted L2 row is purged under the write lock and reads
    // degrade to a miss.
    // =================================================================

    [Fact]
    public async Task CorruptedL2Row_IsPurged_AndTreatedAsMiss()
    {
        var cache = new TwoTierExternalDataCache(_dbContext);
        await cache.SetAsync("corrupt_key", new[] { "payload" });

        // Damage the row directly in SQLite.
        using (var raw = new SqliteConnection($"Data Source={_dbPath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE ExternalDataCache SET DataJson = '{broken-json' WHERE CacheKey = 'corrupt_key';";
            cmd.ExecuteNonQuery();
        }

        // A fresh instance has a cold L1, forcing the corrupt-row path.
        var coldCache = new TwoTierExternalDataCache(_dbContext);
        Assert.Null(await coldCache.GetAsync<string[]>("corrupt_key"));

        // The purge must have REMOVED the row, not just failed to parse it.
        using (var raw = new SqliteConnection($"Data Source={_dbPath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ExternalDataCache WHERE CacheKey = 'corrupt_key';";
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
    }

    // =================================================================
    // ORC-01: a fruitless provider sweep is remembered as a short-TTL
    // negative sentinel — repeat lookups stop re-hitting providers.
    // =================================================================

    [Fact]
    public async Task FetchLyricsAsync_NoLyricsAnywhere_ProviderSweptOnceNotTwice()
    {
        var providerMock = new Mock<IExternalLyricsProvider>();
        providerMock.SetupGet(p => p.ProviderName).Returns("mocklyrics");
        providerMock.SetupGet(p => p.IsEnabled).Returns(true);
        providerMock.SetupGet(p => p.Priority).Returns(1);
        providerMock.Setup(p => p.FetchLyricsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<double?>(), It.IsAny<ExternalIds?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalLyricsResult(null, LyricsState.Unavailable, null, null, "mocklyrics"));

        var orchestrator = new OnlineLyricsOrchestrator(
            new[] { providerMock.Object },
            new TwoTierExternalDataCache(_dbContext));

        var first = await orchestrator.FetchLyricsAsync("Obscure Song", "Obscure Artist");
        Assert.Equal(LyricsState.Unavailable, first.State);

        var second = await orchestrator.FetchLyricsAsync("Obscure Song", "Obscure Artist");
        Assert.Equal(LyricsState.Unavailable, second.State);

        // Pre-ORC-01 both calls swept the provider: negative results were never
        // cached, so every refresh re-hit every enabled provider.
        providerMock.Verify(p => p.FetchLyricsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<double?>(), It.IsAny<ExternalIds?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // =================================================================
    // OL-06: the same real-world track reported by two providers collapses
    // to ONE candidate — by MBID when present, else by the identity triple.
    // =================================================================

    [Fact]
    public async Task SearchTrackCandidatesAsync_SameMbidFromTwoProviders_DedupedToOne()
    {
        IReadOnlyList<TrackMatchCandidate> MbCandidate(string providerName, double confidence) =>
            new[]
            {
                new TrackMatchCandidate(
                    providerName,
                    new ExternalIds(MusicBrainzId: "mb-rec-123"),
                    confidence,
                    "mbid match",
                    new ExternalTrackMetadata("Same Song", "Same Artist", "Same Album", 2020, null, 1, 1, 200.0, null, new ExternalIds(MusicBrainzId: "mb-rec-123")))
            };

        var providerA = new Mock<IExternalMetadataProvider>();
        providerA.SetupGet(p => p.ProviderName).Returns("prov_a");
        providerA.SetupGet(p => p.IsEnabled).Returns(true);
        providerA.SetupGet(p => p.Priority).Returns(1);
        providerA.Setup(p => p.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MbCandidate("prov_a", 0.9));

        var providerB = new Mock<IExternalMetadataProvider>();
        providerB.SetupGet(p => p.ProviderName).Returns("prov_b");
        providerB.SetupGet(p => p.IsEnabled).Returns(true);
        providerB.SetupGet(p => p.Priority).Returns(2);
        providerB.Setup(p => p.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MbCandidate("prov_b", 0.7));

        var orchestrator = new ExternalMetadataOrchestrator(
            new IExternalMetadataProvider[] { providerA.Object, providerB.Object },
            new TwoTierExternalDataCache(_dbContext));

        var results = await orchestrator.SearchTrackCandidatesAsync("Same Song", "Same Artist");

        var single = Assert.Single(results);
        Assert.Equal(0.9, single.Confidence); // highest-confidence representative survives
        providerB.Verify(
            p => p.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchTrackCandidatesAsync_IdentityTripleWithoutMbid_DedupedToOne()
    {
        IReadOnlyList<TrackMatchCandidate> TripleCandidate(string providerName) =>
            new[]
            {
                new TrackMatchCandidate(
                    providerName,
                    new ExternalIds(),
                    0.8,
                    "text match",
                    new ExternalTrackMetadata("Triple Song", "Triple Artist", "  triple album ", 2019, null, 2, 1, 180.0, null, new ExternalIds()))
            };

        var providerA = new Mock<IExternalMetadataProvider>();
        providerA.SetupGet(p => p.ProviderName).Returns("prov_x");
        providerA.SetupGet(p => p.IsEnabled).Returns(true);
        providerA.SetupGet(p => p.Priority).Returns(1);
        providerA.Setup(p => p.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TripleCandidate("prov_x"));

        var providerB = new Mock<IExternalMetadataProvider>();
        providerB.SetupGet(p => p.ProviderName).Returns("prov_y");
        providerB.SetupGet(p => p.IsEnabled).Returns(true);
        providerB.SetupGet(p => p.Priority).Returns(2);
        providerB.Setup(p => p.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TripleCandidate("prov_y"));

        var orchestrator = new ExternalMetadataOrchestrator(
            new IExternalMetadataProvider[] { providerA.Object, providerB.Object },
            new TwoTierExternalDataCache(_dbContext));

        var results = await orchestrator.SearchTrackCandidatesAsync("Triple Song", "Triple Artist", "TRIPLE ALBUM");

        Assert.Single(results);
    }

    // =================================================================
    // ORC-03/OL-08: the manager owns the token layout and memoizes probes.
    // =================================================================

    [Fact]
    public async Task ArtworkCacheManager_TokenResolution_MemoizesExistenceProbes()
    {
        string root = Path.Combine(_tempDir, "artwork_root");
        Directory.CreateDirectory(root);
        var manager = new ArtworkCacheManager(root);

        string? token = await manager.CacheBytesAsync(ValidJpegBytes, "image/jpeg");
        Assert.NotNull(token);

        // Layout resolution is exact: token → <root>/<hash><ext>.
        string resolved = manager.ResolveTokenPath(token!);
        Assert.True(File.Exists(resolved));
        Assert.StartsWith(root, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".jpg", resolved, StringComparison.OrdinalIgnoreCase);

        // First probe is a real hit.
        Assert.True(manager.CachedFileExists(token!));

        // Delete BEHIND the manager's back: within the probe lifetime the answer
        // stays memoized — that is what shields the resolve hot path from
        // repeated synchronous File.Exists calls.
        File.Delete(resolved);
        Assert.True(manager.CachedFileExists(token!));

        // Unknown tokens are a clean false.
        Assert.False(manager.CachedFileExists("ArtworkCache/nope_never_written.jpg"));
    }

    [Fact]
    public async Task ResolveAndCacheAlbumArtworkAsync_FileDeletedBetweenResolves_MemoServesTokenWithoutRefetch()
    {
        string root = Path.Combine(_tempDir, "artwork_memo_root");
        Directory.CreateDirectory(root);
        var manager = new ArtworkCacheManager(root);
        var cache = new TwoTierExternalDataCache(_dbContext);

        var httpHandler = new MockHttpMessageHandlerForArt
        {
            HandlerFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ValidJpegBytes)
            })
        };
        using var httpClient = new HttpClient(httpHandler);
        using var httpService = new HttpService(new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(1)), httpClient);

        var providerMock = new Mock<IExternalAlbumArtworkProvider>();
        providerMock.SetupGet(p => p.ProviderName).Returns("mockart");
        providerMock.SetupGet(p => p.IsEnabled).Returns(true);
        providerMock.SetupGet(p => p.Priority).Returns(1);
        providerMock.Setup(p => p.SearchAlbumArtworkUrlsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ExternalIds?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "http://unit.test/cover.jpg" });

        var orchestrator = new ExternalArtworkOrchestrator(
            new[] { providerMock.Object },
            Array.Empty<IExternalArtistImageProvider>(),
            httpService,
            manager,
            cache);

        string? firstToken = await orchestrator.ResolveAndCacheAlbumArtworkAsync("Memo Album", "Memo Artist");
        Assert.NotNull(firstToken);

        // Simulate external deletion, then re-resolve: the memoized probe serves
        // the cached token without another provider sweep.
        File.Delete(manager.ResolveTokenPath(firstToken!));
        string? secondToken = await orchestrator.ResolveAndCacheAlbumArtworkAsync("Memo Album", "Memo Artist");
        Assert.Equal(firstToken, secondToken);

        providerMock.Verify(p => p.SearchAlbumArtworkUrlsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ExternalIds?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private class MockHttpMessageHandlerForArt : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> HandlerFunc { get; set; } =
            (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            HandlerFunc(request, cancellationToken);
    }

    // =================================================================
    // NF-36: the three "Caching & Networking" settings now have a backend.
    // Previously every SetAsync passed a fixed TTL, the stale fallback was
    // unconditional, and nothing honoured "use cached data offline" — the UI
    // toggles were dead. A null settings service preserves the old hard-coded
    // behaviour (unaffected test call sites); a live one gates offline cache
    // use, the stale fallback, and the retention TTL cap.
    // =================================================================

    // A fully-loaded settings service over this test's DB. The HTTP service is
    // inert here — settings persistence/apply never calls out.
    private ExternalDataSettingsService BuildSettingsService()
    {
        var httpClient = new HttpClient(new MockHttpMessageHandlerForArt());
        var httpService = new HttpService(new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(1)), httpClient);
        var settings = new ExternalDataSettingsService(_dbContext, httpService, new TheAudioDbOptions());
        settings.LoadSettingsAsync().GetAwaiter().GetResult();
        return settings;
    }

    private static Mock<IExternalMetadataProvider> BuildTrackProvider(string name)
    {
        var provider = new Mock<IExternalMetadataProvider>();
        provider.SetupGet(p => p.ProviderName).Returns(name);
        provider.SetupGet(p => p.IsEnabled).Returns(true);
        provider.SetupGet(p => p.Priority).Returns(1);
        return provider;
    }

    private static ExternalTrackMetadata SampleTrackMeta(string title) =>
        new ExternalTrackMetadata(title, "NF36 Artist", "NF36 Album", 2021, null, 1, 1, 210.0, null, new ExternalIds());

    // --- UseCachedDataOffline ------------------------------------------------

    [Fact]
    public async Task MetadataSearch_OfflineOnly_CacheUseDisabled_ServesNothingFromWarmCache()
    {
        var settings = BuildSettingsService();
        var provider = BuildTrackProvider("nf36prov");
        provider.Setup(p => p.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new TrackMatchCandidate("nf36prov", new ExternalIds(), 0.9, "match", SampleTrackMeta("Warm Song")) });

        var orchestrator = new ExternalMetadataOrchestrator(
            new[] { provider.Object }, new TwoTierExternalDataCache(_dbContext), settings);

        // Default settings: the sweep runs and the result is cached.
        var warm = await orchestrator.SearchTrackCandidatesAsync("Warm Song", "NF36 Artist");
        Assert.Single(warm);

        // Offline + "use cached data offline" OFF: even the warm cache is withheld.
        var s = settings.CurrentSettings;
        s.OfflineOnlyMode = true;
        s.UseCachedDataOffline = false;
        await settings.UpdateSettingsAsync(s);

        var offline = await orchestrator.SearchTrackCandidatesAsync("Warm Song", "NF36 Artist");
        Assert.Empty(offline);

        // The short-circuit precedes both the cache read and any provider sweep.
        provider.Verify(p => p.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MetadataSearch_OfflineOnly_CacheUseEnabled_StillServesWarmCache()
    {
        var settings = BuildSettingsService();
        var provider = BuildTrackProvider("nf36prov");
        provider.Setup(p => p.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new TrackMatchCandidate("nf36prov", new ExternalIds(), 0.9, "match", SampleTrackMeta("Warm Song")) });

        var orchestrator = new ExternalMetadataOrchestrator(
            new[] { provider.Object }, new TwoTierExternalDataCache(_dbContext), settings);

        var warm = await orchestrator.SearchTrackCandidatesAsync("Warm Song", "NF36 Artist");
        Assert.Single(warm);

        // Offline ON but cache use LEFT ON (default): the warm cache is served
        // without re-sweeping providers — proving the toggle, not offline mode
        // alone, is what withholds the cache in the test above.
        var s = settings.CurrentSettings;
        s.OfflineOnlyMode = true; // UseCachedDataOffline stays true (default)
        await settings.UpdateSettingsAsync(s);

        var offline = await orchestrator.SearchTrackCandidatesAsync("Warm Song", "NF36 Artist");
        Assert.Single(offline);
        provider.Verify(p => p.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- AllowStaleCacheOnProviderFailure ------------------------------------

    [Fact]
    public async Task GetTrackMetadata_ProviderReturnsNothing_AllowStaleDefault_ServesExpiredEntry()
    {
        var settings = BuildSettingsService(); // AllowStale defaults ON
        var cache = new TwoTierExternalDataCache(_dbContext);

        // Seed an EXPIRED positive entry under the deterministic get-meta key.
        await cache.SetAsync("meta:track:staleprov:e1", SampleTrackMeta("Stale Song"), TimeSpan.FromMilliseconds(40));
        await Task.Delay(120);

        var provider = BuildTrackProvider("staleprov");
        provider.Setup(p => p.GetTrackMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalTrackMetadata?)null);

        var orchestrator = new ExternalMetadataOrchestrator(new[] { provider.Object }, cache, settings);

        var result = await orchestrator.GetTrackMetadataAsync("staleprov", "e1");
        Assert.NotNull(result);
        Assert.Equal("Stale Song", result!.Title);
    }

    [Fact]
    public async Task GetTrackMetadata_ProviderReturnsNothing_AllowStaleDisabled_ReturnsNull()
    {
        var settings = BuildSettingsService();
        var s = settings.CurrentSettings;
        s.AllowStaleCacheOnProviderFailure = false;
        await settings.UpdateSettingsAsync(s);

        var cache = new TwoTierExternalDataCache(_dbContext);
        await cache.SetAsync("meta:track:staleprov:e1", SampleTrackMeta("Stale Song"), TimeSpan.FromMilliseconds(40));
        await Task.Delay(120);

        var provider = BuildTrackProvider("staleprov");
        provider.Setup(p => p.GetTrackMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalTrackMetadata?)null);

        var orchestrator = new ExternalMetadataOrchestrator(new[] { provider.Object }, cache, settings);

        // Stale fallback gated off: the expired entry must NOT be served.
        Assert.Null(await orchestrator.GetTrackMetadataAsync("staleprov", "e1"));
    }

    // --- CacheRetentionDays --------------------------------------------------

    [Fact]
    public async Task GetTrackMetadata_CacheRetentionDays_CapsPositiveTtl()
    {
        var settings = BuildSettingsService();
        var cache = new TwoTierExternalDataCache(_dbContext);

        var provider = BuildTrackProvider("capprov");
        provider.Setup(p => p.GetTrackMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleTrackMeta("Capped Song"));

        var orchestrator = new ExternalMetadataOrchestrator(new[] { provider.Object }, cache, settings);

        // Low retention caps the 30-day get-meta TTL down to ~1 day.
        var low = settings.CurrentSettings;
        low.CacheRetentionDays = 1;
        await settings.UpdateSettingsAsync(low);

        Assert.NotNull(await orchestrator.GetTrackMetadataAsync("capprov", "cap1"));
        var capped = await cache.GetWithMetadataAsync<ExternalTrackMetadata>("meta:track:capprov:cap1", allowStale: true);
        Assert.NotNull(capped);
        var cappedLifetime = capped!.ExpiresAt - capped.CreatedAt;
        Assert.True(cappedLifetime <= TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1), $"expected ~1 day, got {cappedLifetime}");
        Assert.True(cappedLifetime >= TimeSpan.FromHours(23), $"expected ~1 day, got {cappedLifetime}");

        // Control: a generous retention leaves the 30-day TTL intact — the cap
        // only ever shortens, never lengthens.
        var high = settings.CurrentSettings;
        high.CacheRetentionDays = 3650;
        await settings.UpdateSettingsAsync(high);

        Assert.NotNull(await orchestrator.GetTrackMetadataAsync("capprov", "cap2"));
        var uncapped = await cache.GetWithMetadataAsync<ExternalTrackMetadata>("meta:track:capprov:cap2", allowStale: true);
        Assert.NotNull(uncapped);
        var uncappedLifetime = uncapped!.ExpiresAt - uncapped.CreatedAt;
        Assert.True(uncappedLifetime > TimeSpan.FromDays(2), $"expected ~30 days, got {uncappedLifetime}");
    }
}
