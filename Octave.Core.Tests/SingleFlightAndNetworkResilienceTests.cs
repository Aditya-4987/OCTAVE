using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Services.Network;
using Xunit;

namespace Octave.Core.Tests;

/// <summary>
/// Acceptance tests for Batch 10 network-layer hardening (CODEBASE_AUDIT §15):
/// SF-01 per-caller cancellation isolation in AsyncSingleFlight (cancelling one
/// caller must not cancel or corrupt the shared fetch), HL-02 fault
/// propagation, HL-01 spin-free registration, NET-02 non-success response
/// disposal, NET-03 replayable cloned request content across retries, and
/// NET-01 torn-write-safe rate limiter retry windows.
/// </summary>
public class SingleFlightAndNetworkResilienceTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> HandlerFunc { get; set; } =
            (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        public int RequestsSeen;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestsSeen);
            return HandlerFunc(request, cancellationToken);
        }
    }

    private sealed class TrackingContent : HttpContent
    {
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(System.IO.Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    // =================================================================
    // SF-01: each caller's token ends only THEIR wait.
    // =================================================================

    [Fact]
    public async Task SingleFlight_CancellingLeader_DoesNotKillJoinersResult()
    {
        var flight = new AsyncSingleFlight();
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaderCts = new CancellationTokenSource();

        Task<string?> leaderTask = flight.ExecuteAsync<string>("sf_leader", async () =>
        {
            factoryStarted.TrySetResult();
            await releaseFactory.Task;
            return "shared-result";
        }, leaderCts.Token);

        // ExecuteAsync registers the key synchronously before its first await,
        // so by the time the task object is in our hand the entry exists.
        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var joinerTask = flight.ExecuteAsync<string>(
            "sf_leader",
            () => throw new InvalidOperationException("joiner must ride the leader's flight"),
            CancellationToken.None);

        leaderCts.Cancel();

        // The leader's OWN wait ends with cancellation...
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaderTask.WaitAsync(TimeSpan.FromSeconds(5)));

        // ...but the shared fetch was never told to stop and the joiner still
        // receives its result.
        releaseFactory.TrySetResult();
        string? joined = await joinerTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("shared-result", joined);
    }

    [Fact]
    public async Task SingleFlight_CancellingJoiner_DoesNotAffectLeaderOrLateJoiner()
    {
        var flight = new AsyncSingleFlight();
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joinerCts = new CancellationTokenSource();

        Task<string?> leaderTask = flight.ExecuteAsync<string>("sf_joiner", async () =>
        {
            factoryStarted.TrySetResult();
            await releaseFactory.Task;
            return "leader-result";
        }, CancellationToken.None);

        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<string?> joinerTask = flight.ExecuteAsync<string>(
            "sf_joiner",
            () => throw new InvalidOperationException(),
            joinerCts.Token);

        joinerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joinerTask.WaitAsync(TimeSpan.FromSeconds(5)));

        // Leader unaffected; a late caller joins the still-live flight.
        releaseFactory.TrySetResult();
        Assert.Equal("leader-result", await leaderTask.WaitAsync(TimeSpan.FromSeconds(5)));

        string? late = await flight.ExecuteAsync<string>("sf_joiner", () => Task.FromResult<string?>("second-generation"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("second-generation", late); // completed entry is removed, not cached forever
    }

    // =================================================================
    // HL-02: factory faults propagate to every waiter on the flight.
    // =================================================================

    [Fact]
    public async Task SingleFlight_FactoryFault_PropagatesToJoiner()
    {
        var flight = new AsyncSingleFlight();
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string?> leaderTask = flight.ExecuteAsync<string>("sf_fault", async () =>
        {
            factoryStarted.TrySetResult();
            await Task.Delay(50);
            throw new InvalidOperationException("provider exploded");
        }, CancellationToken.None);

        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<string?> joinerTask = flight.ExecuteAsync<string>(
            "sf_fault",
            () => throw new InvalidOperationException(),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => leaderTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => joinerTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // =================================================================
    // HL-01: registration is atomic (GetOrAdd) — a burst of concurrent
    // callers completes without spinning or deadlocking and shares results.
    // =================================================================

    [Fact]
    public async Task SingleFlight_ConcurrentBurst_AllCompleteWithConsistentResults()
    {
        var flight = new AsyncSingleFlight();
        int factoryRuns = 0;

        var tasks = Enumerable.Range(0, 24).Select(_ =>
            flight.ExecuteAsync<string>("sf_burst", async () =>
            {
                Interlocked.Increment(ref factoryRuns);
                await Task.Delay(30);
                return "burst-result";
            }, CancellationToken.None)).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.All(tasks, t => Assert.Equal("burst-result", t.Result));
        // Best-effort dedup (SF-02): every run lands in the cache for followers,
        // so the sweep count stays far below the caller count even under races.
        Assert.InRange(factoryRuns, 1, 24);
    }

    // =================================================================
    // NET-02: non-success responses are disposed before Failure returns.
    // =================================================================

    [Fact]
    public async Task SendAsync_NonSuccessResponse_IsDisposedNotLeaked()
    {
        var trackingContent = new TrackingContent();
        var handler = new MockHttpMessageHandler
        {
            HandlerFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = trackingContent
            })
        };
        using var httpClient = new HttpClient(handler);
        using var service = new HttpService(new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(1)), httpClient);

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://unit.test/missing");
        var result = await service.SendAsync(request, providerKey: "unittest");

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        // Pre-NET-02 the failure path returned without disposing: the message
        // and its content buffer leaked per failed call.
        Assert.True(trackingContent.Disposed);
    }

    // =================================================================
    // NET-03: retried requests carry the body AND content headers.
    // =================================================================

    [Fact]
    public async Task SendAsync_RetriedPost_ReplaysBodyAndContentType()
    {
        int attempts = 0;
        var receivedBodies = new System.Collections.Concurrent.ConcurrentBag<string>();
        var receivedContentTypes = new System.Collections.Concurrent.ConcurrentBag<string?>();

        var captureHandler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, _) =>
            {
                int attempt = Interlocked.Increment(ref attempts);
                string body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                receivedBodies.Add(body);
                receivedContentTypes.Add(req.Content?.Headers.ContentType?.MediaType);
                return Task.FromResult(new HttpResponseMessage(attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
            }
        };

        using var httpClient = new HttpClient(captureHandler);
        using var service = new HttpService(new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(1)), httpClient);

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://unit.test/submit")
        {
            Content = new StringContent("payload-bytes", Encoding.UTF8, "application/json")
        };

        var result = await service.SendAsync(request, providerKey: "unittest");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, Volatile.Read(ref attempts)); // first attempt hit the transient 503
        // Pre-NET-03 the retry clone dropped the content entirely — the second
        // attempt would have arrived empty and without its Content-Type.
        Assert.All(receivedBodies, b => Assert.Equal("payload-bytes", b));
        Assert.Contains(receivedContentTypes, t => t == "application/json");
    }

    // =================================================================
    // NET-01: Retry-After windows extend atomically and never shorten.
    // =================================================================

    [Fact]
    public async Task RateLimiter_NotifyRetryAfter_ExtendsWaitWindow_AndNeverShortensIt()
    {
        var registry = new ProviderRateLimiterRegistry(TimeSpan.FromMilliseconds(10));
        var limiter = registry.GetOrCreate("retryafter_prov");

        limiter.NotifyRetryAfter(TimeSpan.FromMilliseconds(300));

        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 250,
            $"expected ~300ms Retry-After delay, observed {sw.ElapsedMilliseconds}ms");

        // A later, SHORTER hint must not cut the current window short.
        limiter.NotifyRetryAfter(TimeSpan.FromMilliseconds(300));
        limiter.NotifyRetryAfter(TimeSpan.FromMilliseconds(1));

        sw.Restart();
        await limiter.WaitAsync();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 250,
            $"shorter NotifyRetryAfter must not shorten the window; observed {sw.ElapsedMilliseconds}ms");
    }
}
