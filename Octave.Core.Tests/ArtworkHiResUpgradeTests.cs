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

// NF-31: the CAA provider used to prefer 500px thumbnails and the orchestrator
// served whatever was first cached forever - libraries filled up with 500×500
// covers that NowPlaying/backdrop upscaled into blur. These pin the new
// highest-resolution-first preference and the scan-driven self-heal upgrade.
public class ArtworkHiResUpgradeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDbContext _dbContext;

    // Minimal structurally-valid JPEG whose SOF0 frame header declares the
    // given dimensions (enough for ImageValidator's magic-byte check).
    private static byte[] SyntheticJpeg(int width, int height) => new byte[]
    {
        0xFF, 0xD8,
        0xFF, 0xC0, 0x00, 0x0B, 0x08,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        0x01, 0x01, 0x11, 0x00,
        0xFF, 0xD9
    };

    public ArtworkHiResUpgradeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_ArtHiRes_" + Guid.NewGuid().ToString("N"));
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
        public int CallCount;
        public Func<string, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(Responder(request.RequestUri!.ToString()));
        }
    }

    private const string CoverJson =
        @"{ ""images"": [ { ""front"": true, ""image"": ""https://images.example.com/art/full.jpg"" } ] }";

    private static HttpResponseMessage JpegResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } }
    };

    private ExternalArtworkOrchestrator BuildOrchestrator(MockHttpMessageHandler handler)
    {
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, new HttpClient(handler));
        var caaProvider = new CoverArtArchiveArtworkProvider(httpService, null, rateRegistry);
        return new ExternalArtworkOrchestrator(
            new[] { caaProvider },
            Array.Empty<IExternalArtistImageProvider>(),
            httpService,
            new ArtworkCacheManager(_tempDir),
            new TwoTierExternalDataCache(_dbContext));
    }

    // Metadata request → one candidate; the image download → per-call responder.
    private static MockHttpMessageHandler HandlerWithCover(Func<string, HttpResponseMessage> imageResponder) =>
        new()
        {
            Responder = url => url.Contains("/release/")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CoverJson) }
                : imageResponder(url)
        };

    private static ExternalIds ReleaseIds() =>
        new("rel-hires", AdditionalIds: new Dictionary<string, string>
        {
            ["MusicBrainzReleaseId"] = "rel-hires"
        });

    [Fact]
    public async Task Upgrade_LowResCachedToken_ResweepsAndReturnsHigherResolution()
    {
        int servedSize = 500;
        var handler = HandlerWithCover(_ => JpegResponse(SyntheticJpeg(servedSize, servedSize)));
        var orchestrator = BuildOrchestrator(handler);

        // First resolve caches the only offered size: a low-res 500px cover.
        string? first = await orchestrator.ResolveAndCacheAlbumArtworkAsync("Heal Album", "Heal Artist", ReleaseIds());
        Assert.NotNull(first);
        Assert.Equal(500, ImageDimensionReader.TryReadWidth(Path.Combine(_tempDir, first!.Replace("ArtworkCache/", ""))));

        // The provider now offers 1200px; an upgrade-requesting scan must replace
        // the cached low-res file instead of serving it forever.
        servedSize = 1200;

        string? upgraded = await orchestrator.ResolveAndCacheAlbumArtworkAsync(
            "Heal Album", "Heal Artist", ReleaseIds(), preferHighResolutionUpgrade: true);

        Assert.NotNull(upgraded);
        Assert.NotEqual(first, upgraded);
        Assert.Equal(1200, ImageDimensionReader.TryReadWidth(Path.Combine(_tempDir, upgraded!.Replace("ArtworkCache/", ""))));
    }

    [Fact]
    public async Task Upgrade_AlreadyHighRes_ServedWithoutNetwork()
    {
        var handler = HandlerWithCover(_ => JpegResponse(SyntheticJpeg(1280, 1280)));
        var orchestrator = BuildOrchestrator(handler);

        string? token = await orchestrator.ResolveAndCacheAlbumArtworkAsync("Fine Album", "Fine Artist", ReleaseIds());
        Assert.NotNull(token);
        int before = handler.CallCount;

        string? again = await orchestrator.ResolveAndCacheAlbumArtworkAsync(
            "Fine Album", "Fine Artist", ReleaseIds(), preferHighResolutionUpgrade: true);

        Assert.Equal(token, again);
        Assert.Equal(before, handler.CallCount); // hi-res hit: no re-sweep traffic
    }

    [Fact]
    public async Task Upgrade_NothingBetterAvailable_OldTokenServed_CooldownBlocksRepeatSweeps()
    {
        // Only the FIRST image download succeeds (the initial low-res fetch);
        // every later sweep finds nothing better.
        int downloadsDone = 0;
        var handler = HandlerWithCover(url => Interlocked.Exchange(ref downloadsDone, 1) == 0
            ? JpegResponse(SyntheticJpeg(500, 500))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var orchestrator = BuildOrchestrator(handler);

        string? first = await orchestrator.ResolveAndCacheAlbumArtworkAsync("Stuck Album", "Stuck Artist", ReleaseIds());
        Assert.NotNull(first);

        // Upgrade pass #1: sweeps (network fires), finds nothing, falls back.
        int afterFirst = handler.CallCount;
        string? upgraded = await orchestrator.ResolveAndCacheAlbumArtworkAsync(
            "Stuck Album", "Stuck Artist", ReleaseIds(), preferHighResolutionUpgrade: true);
        Assert.Equal(first, upgraded); // stale fallback preserved
        Assert.True(handler.CallCount > afterFirst); // the sweep really ran

        // Upgrade pass #2 within the cooldown: memoized, no network churn.
        int afterSecond = handler.CallCount;
        string? third = await orchestrator.ResolveAndCacheAlbumArtworkAsync(
            "Stuck Album", "Stuck Artist", ReleaseIds(), preferHighResolutionUpgrade: true);
        Assert.Equal(first, third);
        Assert.Equal(afterSecond, handler.CallCount);
    }

    [Fact]
    public async Task NoUpgradeFlag_LowResCachedToken_ServedWithoutNetwork()
    {
        // Playback-time callers keep the plain contract: valid cache = no network.
        var handler = HandlerWithCover(_ => JpegResponse(SyntheticJpeg(500, 500)));
        var orchestrator = BuildOrchestrator(handler);

        string? first = await orchestrator.ResolveAndCacheAlbumArtworkAsync("Plain Album", "Plain Artist", ReleaseIds());
        Assert.NotNull(first);
        int before = handler.CallCount;

        string? again = await orchestrator.ResolveAndCacheAlbumArtworkAsync("Plain Album", "Plain Artist", ReleaseIds());

        Assert.Equal(first, again);
        Assert.Equal(before, handler.CallCount);
    }
}

// NF-31 companion: pure header parsing behind the upgrade decision.
public class ImageDimensionReaderTests
{
    private static string WriteTemp(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"Octave_Dim_{Guid.NewGuid():N}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Png_IhdrWidth_IsRead()
    {
        // PNG signature + IHDR chunk declaring 640×480.
        byte[] png =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x02, 0x80, // width 640
            0x00, 0x00, 0x01, 0xE0, // height 480
            0x08, 0x06, 0x00, 0x00, 0x00
        };
        string path = WriteTemp(png);
        try
        {
            Assert.Equal(640, ImageDimensionReader.TryReadWidth(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Jpeg_SofFrameWidth_IsRead()
    {
        string path = WriteTemp(new byte[]
        {
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x0B, 0x08,
            0x04, 0x38, // height 1080
            0x04, 0xB0, // width 1200
            0x01, 0x01, 0x11, 0x00,
            0xFF, 0xD9
        });
        try
        {
            Assert.Equal(1200, ImageDimensionReader.TryReadWidth(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Garbage_MissingFile_Truncated_NeverThrows_ReturnsNull()
    {
        string garbagePath = WriteTemp(new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A });
        try
        {
            Assert.Null(ImageDimensionReader.TryReadWidth(garbagePath));
        }
        finally
        {
            File.Delete(garbagePath);
        }

        Assert.Null(ImageDimensionReader.TryReadWidth(null));
        Assert.Null(ImageDimensionReader.TryReadWidth(""));
        Assert.Null(ImageDimensionReader.TryReadWidth("Z:/definitely/not/here.jpg"));

        string truncatedPath = WriteTemp(new byte[] { 0xFF, 0xD8 });
        try
        {
            Assert.Null(ImageDimensionReader.TryReadWidth(truncatedPath));
        }
        finally
        {
            File.Delete(truncatedPath);
        }
    }
}
