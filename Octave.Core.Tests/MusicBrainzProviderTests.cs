using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Services.External.MusicBrainz;
using Octave.Core.Services.Network;
using Xunit;

namespace Octave.Core.Tests;

public class MusicBrainzProviderTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> HandlerFunc { get; set; } =
            (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            HandlerFunc(request, cancellationToken);
    }

    // =================================================================
    // 1. SUCCESSFUL RECORDING LOOKUP & SEARCH
    // =================================================================

    [Fact]
    public async Task SearchTrackCandidatesAsync_SuccessfulSearch_ReturnsNormalizedCandidates()
    {
        string jsonResponse = @"
        {
            ""count"": 1,
            ""offset"": 0,
            ""recordings"": [
                {
                    ""id"": ""rec-12345"",
                    ""score"": 100,
                    ""title"": ""Bohemian Rhapsody"",
                    ""length"": 354000,
                    ""first-release-date"": ""1975-10-31"",
                    ""isrcs"": [""GBUM71029603""],
                    ""artist-credit"": [
                        {
                            ""name"": ""Queen"",
                            ""artist"": { ""id"": ""art-queen-99"", ""name"": ""Queen"" }
                        }
                    ],
                    ""releases"": [
                        {
                            ""id"": ""rel-opera-11"",
                            ""title"": ""A Night at the Opera"",
                            ""date"": ""1975-11-21"",
                            ""release-group"": { ""id"": ""rg-opera-00"" }
                        }
                    ],
                    ""genres"": [
                        { ""name"": ""Progressive Rock"" }
                    ]
                }
            ]
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Assert.Contains("recording?query=", req.RequestUri!.ToString());
                Assert.Contains("fmt=json", req.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(jsonResponse)
                });
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var provider = new MusicBrainzMetadataProvider(httpService, rateRegistry);

        var results = await provider.SearchTrackCandidatesAsync("Bohemian Rhapsody", "Queen", "A Night at the Opera", 354.0);

        Assert.Single(results);
        var candidate = results[0];
        Assert.Equal("MusicBrainz", candidate.ProviderName);
        Assert.Equal("rec-12345", candidate.ExternalIds.MusicBrainzId);
        Assert.Equal("GBUM71029603", candidate.ExternalIds.Isrc);
        Assert.Equal("rel-opera-11", candidate.ExternalIds.GetId("MusicBrainzReleaseId"));
        Assert.Equal("art-queen-99", candidate.ExternalIds.GetId("MusicBrainzArtistId"));
        Assert.Equal("Bohemian Rhapsody", candidate.Metadata.Title);
        Assert.Equal("Queen", candidate.Metadata.ArtistName);
        Assert.Equal("A Night at the Opera", candidate.Metadata.AlbumTitle);
        Assert.Equal(1975, candidate.Metadata.Year);
        Assert.Equal("Progressive Rock", candidate.Metadata.Genre);
        Assert.True(candidate.Confidence >= 0.95);
    }

    [Fact]
    public async Task GetTrackMetadataAsync_ById_ReturnsDetailedMetadata()
    {
        string jsonResponse = @"
        {
            ""id"": ""rec-12345"",
            ""title"": ""Bohemian Rhapsody"",
            ""length"": 354000,
            ""first-release-date"": ""1975"",
            ""isrcs"": [""GBUM71029603""],
            ""artist-credit"": [
                {
                    ""name"": ""Queen"",
                    ""artist"": { ""id"": ""art-queen-99"" }
                }
            ],
            ""releases"": [
                {
                    ""id"": ""rel-opera-11"",
                    ""title"": ""A Night at the Opera""
                }
            ]
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Assert.Contains("recording/rec-12345", req.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(jsonResponse)
                });
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var provider = new MusicBrainzMetadataProvider(httpService, rateRegistry);

        var meta = await provider.GetTrackMetadataAsync("rec-12345");

        Assert.NotNull(meta);
        Assert.Equal("rec-12345", meta.ExternalIds.MusicBrainzId);
        Assert.Equal("Bohemian Rhapsody", meta.Title);
        Assert.Equal("Queen", meta.ArtistName);
        Assert.Equal("A Night at the Opera", meta.AlbumTitle);
        Assert.Equal("GBUM71029603", meta.Isrc);
    }

    // =================================================================
    // 2. ARTIST LOOKUP & SEARCH
    // =================================================================

    [Fact]
    public async Task SearchArtistCandidatesAsync_ReturnsNormalizedArtists()
    {
        string jsonResponse = @"
        {
            ""count"": 1,
            ""artists"": [
                {
                    ""id"": ""art-queen-99"",
                    ""name"": ""Queen"",
                    ""country"": ""GB"",
                    ""score"": 100,
                    ""disambiguation"": ""legendary UK rock band"",
                    ""life-span"": { ""begin"": ""1970"", ""ended"": false }
                }
            ]
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse)
            })
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var provider = new MusicBrainzMetadataProvider(httpService, rateRegistry);

        var candidates = await provider.SearchArtistCandidatesAsync("Queen");

        Assert.Single(candidates);
        var art = candidates[0];
        Assert.Equal("art-queen-99", art.ExternalIds.MusicBrainzId);
        Assert.Equal("Queen", art.Metadata.Name);
        Assert.Equal("GB", art.Metadata.Country);
        Assert.Equal(1970, art.Metadata.FormedYear);
        Assert.Null(art.Metadata.DisbandedYear);
        Assert.Equal("legendary UK rock band", art.Metadata.Bio);
    }

    // =================================================================
    // 3. ALBUM / RELEASE LOOKUP & SEARCH
    // =================================================================

    [Fact]
    public async Task GetAlbumMetadataAsync_ReturnsReleaseDetailsAndTracklist()
    {
        string jsonResponse = @"
        {
            ""id"": ""rel-opera-11"",
            ""title"": ""A Night at the Opera"",
            ""date"": ""1975-11-21"",
            ""artist-credit"": [ { ""name"": ""Queen"" } ],
            ""release-group"": { ""id"": ""rg-opera-00"" },
            ""media"": [
                {
                    ""position"": 1,
                    ""tracks"": [
                        {
                            ""number"": ""1"",
                            ""title"": ""Death on Two Legs"",
                            ""length"": 223000,
                            ""recording"": { ""id"": ""rec-track-1"" }
                        },
                        {
                            ""number"": ""2"",
                            ""title"": ""Lazing on a Sunday Afternoon"",
                            ""length"": 67000,
                            ""recording"": { ""id"": ""rec-track-2"" }
                        }
                    ]
                }
            ]
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse)
            })
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var provider = new MusicBrainzMetadataProvider(httpService, rateRegistry);

        var album = await provider.GetAlbumMetadataAsync("rel-opera-11");

        Assert.NotNull(album);
        Assert.Equal("A Night at the Opera", album.Title);
        Assert.Equal("Queen", album.ArtistName);
        Assert.Equal(1975, album.Year);
        Assert.NotNull(album.Tracklist);
        Assert.Equal(2, album.Tracklist.Count);
        Assert.Equal("Death on Two Legs", album.Tracklist[0].Title);
        Assert.Equal(1, album.Tracklist[0].TrackNumber);
        Assert.Equal("rec-track-1", album.Tracklist[0].ExternalIds.MusicBrainzId);
    }

    // =================================================================
    // 4. NO-RESULT RESPONSE
    // =================================================================

    [Fact]
    public async Task SearchTrackCandidatesAsync_NoResults_ReturnsEmptyList()
    {
        string emptyJson = @"{ ""count"": 0, ""recordings"": [] }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(emptyJson)
            })
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var provider = new MusicBrainzMetadataProvider(httpService, rateRegistry);

        var candidates = await provider.SearchTrackCandidatesAsync("CompletelyNonexistentSong123", "Nobody");

        Assert.Empty(candidates);
    }

    // =================================================================
    // 5. MALFORMED JSON RESPONSE
    // =================================================================

    [Fact]
    public async Task SearchTrackCandidatesAsync_MalformedJson_HandlesGracefullyWithoutThrowing()
    {
        string badJson = @"<html><body>502 Bad Gateway from NGINX</body></html>";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(badJson)
            })
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var provider = new MusicBrainzMetadataProvider(httpService, rateRegistry);

        var candidates = await provider.SearchTrackCandidatesAsync("Bohemian Rhapsody", "Queen");

        Assert.Empty(candidates);
    }

    // =================================================================
    // 6. CANCELLATION
    // =================================================================

    [Fact]
    public async Task SearchTrackCandidatesAsync_CancelledToken_ReturnsEmptyOrThrowsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var provider = new MusicBrainzMetadataProvider(httpService, rateRegistry);

        var candidates = await provider.SearchTrackCandidatesAsync("Bohemian Rhapsody", "Queen", ct: cts.Token);

        Assert.Empty(candidates);
    }

    // =================================================================
    // 7. RATE LIMITING (Spaces requests by configured interval)
    // =================================================================

    [Fact]
    public async Task ProviderRateLimiter_EnforcesMinimumIntervalBetweenRequests()
    {
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(80));
        var limiter = rateRegistry.GetOrCreate("musicbrainz", TimeSpan.FromMilliseconds(80));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await limiter.WaitAsync();
        await limiter.WaitAsync();
        await limiter.WaitAsync();

        stopwatch.Stop();

        // 3 consecutive calls with 80ms interval must take at least ~140ms
        Assert.True(stopwatch.ElapsedMilliseconds >= 140, $"Expected >= 140ms, got {stopwatch.ElapsedMilliseconds}ms");
    }

    // =================================================================
    // 8. DUPLICATE / IN-FLIGHT REQUEST DEDUPLICATION
    // =================================================================

    [Fact]
    public async Task SearchTrackCandidatesAsync_ConcurrentDuplicateRequests_DeduplicatesToSingleHttpCall()
    {
        int networkCallCount = 0;
        string jsonResponse = @"{ ""count"": 1, ""recordings"": [ { ""id"": ""rec-1"", ""title"": ""Radio Ga Ga"", ""score"": 100 } ] }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = async (req, ct) =>
            {
                Interlocked.Increment(ref networkCallCount);
                await Task.Delay(100, ct); // simulate network latency
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(jsonResponse)
                };
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry();
        var httpService = new HttpService(rateRegistry, httpClient);
        var provider = new MusicBrainzMetadataProvider(httpService, rateRegistry);

        // Fire 4 identical search queries simultaneously
        var task1 = provider.SearchTrackCandidatesAsync("Radio Ga Ga", "Queen");
        var task2 = provider.SearchTrackCandidatesAsync("Radio Ga Ga", "Queen");
        var task3 = provider.SearchTrackCandidatesAsync("Radio Ga Ga", "Queen");
        var task4 = provider.SearchTrackCandidatesAsync("Radio Ga Ga", "Queen");

        var allResults = await Task.WhenAll(task1, task2, task3, task4);

        // Verify all callers received valid data
        foreach (var res in allResults)
        {
            Assert.Single(res);
            Assert.Equal("Radio Ga Ga", res[0].Metadata.Title);
        }

        // Verify only 1 actual HTTP request was dispatched across the network
        Assert.Equal(1, networkCallCount);
    }

    // =================================================================
    // 9. BATCH 7 — ARTIST-AWARE CONFIDENCE (MB-02) & REAL TRACK POSITION (MB-03)
    // =================================================================

    [Fact]
    public async Task SearchTrackCandidatesAsync_WrongArtistSameTitle_ScoresFarBelowCorrectArtist()
    {
        string jsonResponse = @"
        {
            ""count"": 2,
            ""recordings"": [
                {
                    ""id"": ""rec-right"",
                    ""score"": 100,
                    ""title"": ""Time"",
                    ""artist-credit"": [ { ""name"": ""Pink Floyd"", ""artist"": { ""id"": ""art-pf"" } } ],
                    ""releases"": [ { ""id"": ""rel-dsom"", ""title"": ""The Dark Side of the Moon"" } ]
                },
                {
                    ""id"": ""rec-cover"",
                    ""score"": 100,
                    ""title"": ""Time"",
                    ""artist-credit"": [ { ""name"": ""Zzyxx Cover Band"", ""artist"": { ""id"": ""art-zz"" } } ],
                    ""releases"": [ { ""id"": ""rel-other"", ""title"": ""Totally Different Album"" } ]
                }
            ]
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse)
            })
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var provider = new MusicBrainzMetadataProvider(httpService, rateRegistry);

        var results = await provider.SearchTrackCandidatesAsync("Time", "Pink Floyd", "The Dark Side of the Moon", 413.0);

        Assert.Equal(2, results.Count);

        var right = results.Single(r => r.ExternalIds.MusicBrainzId == "rec-right");
        var cover = results.Single(r => r.ExternalIds.MusicBrainzId == "rec-cover");

        Assert.True(right.Confidence >= 0.95, $"Expected correct artist >= 0.95, got {right.Confidence}");

        // MB-02: identical search-score + identical title must not carry a
        // different artist's recording into an auto-apply tier. The artist fold
        // halves the base before boosts, and the album mismatch subtracts more.
        Assert.True(cover.Confidence <= 0.60, $"Expected cover/tribute <= 0.60, got {cover.Confidence}");
        Assert.True(right.Confidence - cover.Confidence >= 0.30,
            $"Expected a decisive gap, got {right.Confidence} vs {cover.Confidence}");
    }

    [Fact]
    public async Task SearchTrackCandidatesAsync_TrackLivesInSecondMedium_ReportsRealPosition()
    {
        string jsonResponse = @"
        {
            ""count"": 1,
            ""recordings"": [
                {
                    ""id"": ""rec-pos"",
                    ""score"": 100,
                    ""title"": ""Time"",
                    ""artist-credit"": [ { ""name"": ""Pink Floyd"" } ],
                    ""releases"": [
                        {
                            ""id"": ""rel-x"",
                            ""title"": ""The Dark Side of the Moon"",
                            ""media"": [
                                {
                                    ""position"": 1,
                                    ""tracks"": [
                                        { ""number"": ""1"", ""title"": ""Intro"", ""recording"": { ""id"": ""rec-someoneelse"" } }
                                    ]
                                },
                                {
                                    ""position"": 2,
                                    ""tracks"": [
                                        { ""number"": ""1"", ""title"": ""Speak To Me"", ""recording"": { ""id"": ""rec-other-disc-track"" } },
                                        { ""number"": ""3"", ""title"": ""Time"", ""recording"": { ""id"": ""rec-pos"" } }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        }";

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse)
            })
        };

        var httpClient = new HttpClient(mockHandler);
        var rateRegistry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var httpService = new HttpService(rateRegistry, httpClient);
        var provider = new MusicBrainzMetadataProvider(httpService, rateRegistry);

        var results = await provider.SearchTrackCandidatesAsync("Time", "Pink Floyd");

        Assert.Single(results);

        // MB-03: position comes from the medium whose track list actually names
        // THIS recording id — not "first medium's first track" (which reported
        // 1/1 for anything with multi-medium releases).
        Assert.Equal(3, results[0].Metadata.TrackNumber);
        Assert.Equal(2, results[0].Metadata.DiscNumber);

        // Entity-kind stamping: recording MBIDs declare themselves so downstream
        // consumers (CoverArtArchive routing) never mistake them for releases.
        Assert.Equal(ExternalIdKinds.KindRecording, results[0].ExternalIds.GetId(ExternalIdKinds.EntityKind));
    }
}
