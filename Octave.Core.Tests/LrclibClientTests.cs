using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;
using Octave.Core.Services.Metadata;
using Xunit;

namespace Octave.Core.Tests;

public class LrclibClientTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> HandlerFunc { get; set; }

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc)
        {
            HandlerFunc = handlerFunc;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return HandlerFunc(request, cancellationToken);
        }
    }

    [Fact]
    public async Task GetLyricsAsync_SuccessfulResponse_WithBothStaticAndSynced_ParsesBothCorrectly()
    {
        // 1. Successful response containing both static and synchronized lyrics
        string json = @"
{
  ""id"": 3396226,
  ""name"": ""I Want to Live"",
  ""trackName"": ""I Want to Live"",
  ""artistName"": ""Borislav Slavov"",
  ""albumName"": ""Baldur's Gate 3"",
  ""duration"": 233,
  ""instrumental"": false,
  ""plainLyrics"": ""I feel your breath upon my neck\nThe clock won't stop\n"",
  ""syncedLyrics"": ""[00:17.12] I feel your breath upon my neck\n[00:22.30] The clock won't stop\n""
}";

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var lrclib = new LrclibClient(client);

        var result = await lrclib.GetLyricsAsync("I Want to Live", "Borislav Slavov", "Baldur's Gate 3", 233.2);

        Assert.NotNull(result);
        Assert.Equal(3396226, result.Id);
        Assert.Equal("I Want to Live", result.TrackName);
        Assert.Equal("Borislav Slavov", result.ArtistName);
        Assert.Equal("Baldur's Gate 3", result.AlbumName);
        Assert.Equal(233, result.Duration);
        Assert.False(result.Instrumental);
        Assert.Contains("I feel your breath", result.PlainLyrics);
        Assert.Contains("[00:17.12]", result.SyncedLyrics);
    }

    [Fact]
    public async Task GetLyricsAsync_ResponseWithOnlyStaticLyrics_ParsesPlainAndNullSynced()
    {
        // 2. Response containing only static lyrics
        string json = @"
{
  ""id"": 12345,
  ""trackName"": ""Acoustic Song"",
  ""artistName"": ""Solo Artist"",
  ""albumName"": ""Acoustics"",
  ""duration"": 180,
  ""instrumental"": false,
  ""plainLyrics"": ""Only plain text lyrics here."",
  ""syncedLyrics"": null
}";

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var lrclib = new LrclibClient(client);

        var result = await lrclib.GetLyricsAsync("Acoustic Song", "Solo Artist");

        Assert.NotNull(result);
        Assert.Equal("Only plain text lyrics here.", result.PlainLyrics);
        Assert.Null(result.SyncedLyrics);
        Assert.False(result.Instrumental);
    }

    [Fact]
    public async Task GetLyricsAsync_ResponseWithOnlySyncedLyrics_ParsesSyncedAndNullPlain()
    {
        // 3. Response containing only synchronized lyrics
        string json = @"
{
  ""id"": 67890,
  ""trackName"": ""Synced Only Song"",
  ""artistName"": ""Synth Artist"",
  ""albumName"": null,
  ""duration"": 210,
  ""instrumental"": false,
  ""plainLyrics"": null,
  ""syncedLyrics"": ""[00:10.00] Synced line 1\n[00:20.00] Synced line 2""
}";

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var lrclib = new LrclibClient(client);

        var result = await lrclib.GetLyricsAsync("Synced Only Song", "Synth Artist");

        Assert.NotNull(result);
        Assert.Null(result.PlainLyrics);
        Assert.Equal("[00:10.00] Synced line 1\n[00:20.00] Synced line 2", result.SyncedLyrics);
    }

    [Fact]
    public async Task GetLyricsAsync_ResponseWithNeither_OrInstrumental_ParsesCorrectly()
    {
        // 4. Response containing neither (e.g. instrumental track)
        string json = @"
{
  ""id"": 99999,
  ""trackName"": ""Orchestral Theme"",
  ""artistName"": ""Orchestra"",
  ""albumName"": ""Soundtrack"",
  ""duration"": 300,
  ""instrumental"": true,
  ""plainLyrics"": null,
  ""syncedLyrics"": null
}";

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var lrclib = new LrclibClient(client);

        var result = await lrclib.GetLyricsAsync("Orchestral Theme", "Orchestra");

        Assert.NotNull(result);
        Assert.True(result.Instrumental);
        Assert.Null(result.PlainLyrics);
        Assert.Null(result.SyncedLyrics);
    }

    [Fact]
    public async Task GetLyricsAsync_TrackNotFound_404_ReturnsNull()
    {
        // 5. Track not found (404)
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(@"{""code"":404,""name"":""TrackNotFound"",""message"":""Failed to find specified track""}", System.Text.Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var lrclib = new LrclibClient(client);

        var result = await lrclib.GetLyricsAsync("Nonexistent Song", "Unknown Artist");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLyricsAsync_NetworkOrApiFailure_ThrowsHttpRequestException()
    {
        // 6. Network/API failure (e.g. 500 internal server error or connection failure)
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Server Error", System.Text.Encoding.UTF8, "text/plain")
            });
        });

        using var client = new HttpClient(handler);
        var lrclib = new LrclibClient(client);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            lrclib.GetLyricsAsync("Test Song", "Test Artist"));
    }

    [Fact]
    public async Task GetLyricsAsync_CorrectRequestConstruction_FromTrackMetadata()
    {
        // 7. Correct construction of the LRCLIB request from track metadata
        HttpRequestMessage? capturedRequest = null;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            capturedRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var client = new HttpClient(handler);
        var lrclib = new LrclibClient(client);

        await lrclib.GetLyricsAsync(
            trackTitle: "Rock & Roll / Live",
            artistName: "AC/DC & Guests",
            albumName: "Live Album #1",
            durationSeconds: 245.8);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Get, capturedRequest.Method);

        string query = capturedRequest.RequestUri!.Query;
        Assert.Contains("track_name=" + Uri.EscapeDataString("Rock & Roll / Live"), query);
        Assert.Contains("artist_name=" + Uri.EscapeDataString("AC/DC & Guests"), query);
        Assert.Contains("album_name=" + Uri.EscapeDataString("Live Album #1"), query);
        Assert.Contains("duration=246", query); // 245.8 rounded to 246

        // Check User-Agent header
        Assert.True(capturedRequest.Headers.Contains("User-Agent"));
        string userAgent = string.Join(" ", capturedRequest.Headers.GetValues("User-Agent"));
        Assert.Contains("OCTAVE", userAgent);
    }

    [Fact]
    public async Task GetLyricsAsync_CorrectResponseParsing_AllFields()
    {
        // 8. Correct parsing of the LRCLIB response
        string json = @"
{
  ""id"": 456789,
  ""name"": ""Bohemian Rhapsody"",
  ""trackName"": ""Bohemian Rhapsody"",
  ""artistName"": ""Queen"",
  ""albumName"": ""A Night at the Opera"",
  ""duration"": 354,
  ""instrumental"": false,
  ""plainLyrics"": ""Is this the real life?\nIs this just fantasy?"",
  ""syncedLyrics"": ""[00:00.95] Is this the real life?\n[00:04.28] Is this just fantasy?""
}";

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var lrclib = new LrclibClient(client);

        var result = await lrclib.GetLyricsAsync("Bohemian Rhapsody", "Queen", "A Night at the Opera", 354);

        Assert.NotNull(result);
        Assert.Equal(456789, result.Id);
        Assert.Equal("Bohemian Rhapsody", result.TrackName);
        Assert.Equal("Queen", result.ArtistName);
        Assert.Equal("A Night at the Opera", result.AlbumName);
        Assert.Equal(354, result.Duration);
        Assert.False(result.Instrumental);
        Assert.Equal("Is this the real life?\nIs this just fantasy?", result.PlainLyrics);
        Assert.Equal("[00:00.95] Is this the real life?\n[00:04.28] Is this just fantasy?", result.SyncedLyrics);
    }
}
