using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Octave.Core.Services.Network;

public class HttpService : IHttpService, IDisposable
{
    // PROV-01: MusicBrainz and CoverArtArchive require an identifying
    // User-Agent with real contact information; a bare "OCTAVE/2.0" product
    // token risks throttling/blocks under their usage policies.
    public const string UserAgentString = "OCTAVE/2.0 (https://github.com/Aditya-4987/OCTAVE)";

    // PROV-02: authoritative per-provider minimum spacing enforced at THIS
    // layer. The shared registry's 500 ms default can lose to construction
    // order — a provider ctor calling registry.GetOrCreate(key, 1 s) only wins
    // if it is FIRST for that key, so the stricter floor must not depend on it.
    // MusicBrainz: max 1 req/s average (serially 1/s). CoverArtArchive: same
    // limit, mirrored from the CAA provider's own intent.
    private static readonly Dictionary<string, TimeSpan> ProviderMinIntervals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["musicbrainz"] = TimeSpan.FromSeconds(1),
        ["coverartarchive"] = TimeSpan.FromSeconds(1),
    };

    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private readonly IProviderRateLimiterRegistry _rateLimiterRegistry;
    private readonly ConcurrentDictionary<string, IProviderRateLimiter> _policyLimiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _defaultTimeout;
    private readonly int _maxRetries;
    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public HttpService(
        IProviderRateLimiterRegistry rateLimiterRegistry,
        HttpClient? httpClient = null,
        TimeSpan? defaultTimeout = null,
        int maxRetries = 3)
    {
        _rateLimiterRegistry = rateLimiterRegistry ?? throw new ArgumentNullException(nameof(rateLimiterRegistry));
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(10);
        _maxRetries = Math.Max(0, maxRetries);

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.All
            };
            _httpClient = new HttpClient(handler, disposeHandler: true);
            _disposeClient = true;
        }

        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            // PROV-01: product + contact-comment form (RFC 7231). Set without
            // validation because ProductInfoHeaderValue cannot represent the
            // "(contact)" comment in one token pair.
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgentString);
        }
    }

    public async Task<HttpResult<string>> GetStringAsync(
        string url,
        string? providerKey = null,
        IDictionary<string, string>? customHeaders = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (customHeaders != null)
        {
            foreach (var kvp in customHeaders)
            {
                request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
            }
        }

        var responseResult = await SendAsync(request, providerKey, timeout, ct).ConfigureAwait(false);
        if (!responseResult.IsSuccess || responseResult.Data == null)
        {
            return HttpResult<string>.Failure(
                responseResult.ErrorMessage ?? "HTTP request failed",
                responseResult.StatusCode,
                responseResult.RetryAfter);
        }

        using var response = responseResult.Data;
        try
        {
            string content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return HttpResult<string>.Success(content, response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HttpResult<string>.Failure($"Error reading response content: {ex.Message}", response.StatusCode);
        }
    }

    public async Task<HttpResult<byte[]>> GetByteArrayAsync(
        string url,
        string? providerKey = null,
        IDictionary<string, string>? customHeaders = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (customHeaders != null)
        {
            foreach (var kvp in customHeaders)
            {
                request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
            }
        }

        var responseResult = await SendAsync(request, providerKey, timeout, ct).ConfigureAwait(false);
        if (!responseResult.IsSuccess || responseResult.Data == null)
        {
            return HttpResult<byte[]>.Failure(
                responseResult.ErrorMessage ?? "HTTP request failed",
                responseResult.StatusCode,
                responseResult.RetryAfter);
        }

        using var response = responseResult.Data;
        try
        {
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return HttpResult<byte[]>.Success(bytes, response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HttpResult<byte[]>.Failure($"Error reading response bytes: {ex.Message}", response.StatusCode);
        }
    }

    public async Task<HttpResult<T>> GetJsonAsync<T>(
        string url,
        string? providerKey = null,
        IDictionary<string, string>? customHeaders = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var stringResult = await GetStringAsync(url, providerKey, customHeaders, timeout, ct).ConfigureAwait(false);
        if (!stringResult.IsSuccess || stringResult.Data == null)
        {
            return HttpResult<T>.Failure(
                stringResult.ErrorMessage ?? "Failed to fetch JSON response",
                stringResult.StatusCode,
                stringResult.RetryAfter);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<T>(stringResult.Data, DefaultJsonOptions);
            if (parsed == null)
            {
                return HttpResult<T>.Failure("JSON response deserialized to null", stringResult.StatusCode);
            }
            return HttpResult<T>.Success(parsed, stringResult.StatusCode ?? HttpStatusCode.OK);
        }
        catch (JsonException jEx)
        {
            return HttpResult<T>.Failure($"JSON deserialization error: {jEx.Message}", stringResult.StatusCode);
        }
    }

    public async Task<HttpResult<HttpResponseMessage>> SendAsync(
        HttpRequestMessage request,
        string? providerKey = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        string limiterKey = providerKey ?? request.RequestUri?.Host ?? "default";
        IProviderRateLimiter rateLimiter = GetAuthoritativeLimiter(limiterKey);
        TimeSpan effectiveTimeout = timeout ?? _defaultTimeout;

        int attempt = 0;
        var rng = new Random();

        while (true)
        {
            attempt++;
            try
            {
                // Rate limit spacing
                await rateLimiter.WaitAsync(ct).ConfigureAwait(false);

                using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                using var currentReq = CloneRequest(request);

                var response = await _httpClient.SendAsync(currentReq, linkedCts.Token).ConfigureAwait(false);

                // Check for Retry-After header
                TimeSpan? retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
                if (retryAfter.HasValue)
                {
                    rateLimiter.NotifyRetryAfter(retryAfter.Value);
                }

                // Check transient status codes that warrant retry
                if (IsTransientStatusCode(response.StatusCode) && attempt <= _maxRetries)
                {
                    response.Dispose();
                    TimeSpan backoff = CalculateBackoff(attempt, retryAfter, rng);
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    string error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    return HttpResult<HttpResponseMessage>.Failure(error, response.StatusCode, retryAfter);
                }

                return HttpResult<HttpResponseMessage>.Success(response, response.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return HttpResult<HttpResponseMessage>.Cancelled();
            }
            catch (OperationCanceledException)
            {
                // Timeout occurred
                if (attempt <= _maxRetries)
                {
                    TimeSpan backoff = CalculateBackoff(attempt, null, rng);
                    try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch { return HttpResult<HttpResponseMessage>.Cancelled(); }
                    continue;
                }
                return HttpResult<HttpResponseMessage>.Failure($"Request timed out after {effectiveTimeout.TotalSeconds:0.##}s", HttpStatusCode.RequestTimeout);
            }
            catch (HttpRequestException httpEx)
            {
                if (attempt <= _maxRetries)
                {
                    TimeSpan backoff = CalculateBackoff(attempt, null, rng);
                    try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch { return HttpResult<HttpResponseMessage>.Cancelled(); }
                    continue;
                }
                return HttpResult<HttpResponseMessage>.Failure($"Network error: {httpEx.Message}", httpEx.StatusCode);
            }
            catch (Exception ex)
            {
                return HttpResult<HttpResponseMessage>.Failure($"Unexpected request failure: {ex.Message}");
            }
        }
    }

    private static bool IsTransientStatusCode(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests || // 429
        code == HttpStatusCode.BadGateway ||       // 502
        code == HttpStatusCode.ServiceUnavailable || // 503
        code == HttpStatusCode.GatewayTimeout;    // 504

    // PROV-02: resolve the limiter for a provider key, upgrading the registry's
    // instance when this layer's policy floor is stricter. Decorators are cached
    // per key — their spacing state must survive across calls; creating one per
    // request would reset it and permit bursts.
    private IProviderRateLimiter GetAuthoritativeLimiter(string key)
    {
        IProviderRateLimiter registryLimiter = _rateLimiterRegistry.GetOrCreate(key);

        if (!ProviderMinIntervals.TryGetValue(key, out TimeSpan minInterval) ||
            minInterval <= registryLimiter.MinInterval)
        {
            return registryLimiter;
        }

        return _policyLimiters.GetOrAdd(
            key,
            static (_, args) => new MinIntervalRateLimiter(args.Inner, args.Interval),
            (Inner: registryLimiter, Interval: minInterval));
    }

    // PROV-02: serializing decorator that guarantees a minimum gap between
    // consecutive sends for its key, regardless of what the shared registry's
    // default is. Retry-After signals extend the floor on both layers.
    private sealed class MinIntervalRateLimiter : IProviderRateLimiter
    {
        private readonly IProviderRateLimiter _inner;
        private readonly TimeSpan _minInterval;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private long _nextAllowedTicks;

        public string ProviderKey => _inner.ProviderKey;
        public TimeSpan MinInterval => _minInterval;

        public MinIntervalRateLimiter(IProviderRateLimiter inner, TimeSpan minInterval)
        {
            _inner = inner;
            _minInterval = minInterval;
            // First call passes immediately.
            _nextAllowedTicks = Environment.TickCount64 - (long)minInterval.TotalMilliseconds;
        }

        public async Task WaitAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                long waitMs = Interlocked.Read(ref _nextAllowedTicks) - Environment.TickCount64;
                if (waitMs > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
                }

                // The inner (registry) limiter keeps its own bookkeeping in sync;
                // its interval is never longer than ours here by construction.
                await _inner.WaitAsync(ct).ConfigureAwait(false);

                Interlocked.Exchange(ref _nextAllowedTicks, Environment.TickCount64 + (long)_minInterval.TotalMilliseconds);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void NotifyRetryAfter(TimeSpan retryAfter)
        {
            _inner.NotifyRetryAfter(retryAfter);

            long extensionMs = (long)retryAfter.TotalMilliseconds;
            if (extensionMs <= 0) return;

            long target = Environment.TickCount64 + extensionMs;
            long current = Interlocked.Read(ref _nextAllowedTicks);
            if (target > current)
            {
                Interlocked.Exchange(ref _nextAllowedTicks, target);
            }
        }
    }

    private static TimeSpan CalculateBackoff(int attempt, TimeSpan? retryAfter, Random rng)
    {
        if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
        {
            return retryAfter.Value;
        }

        // Bounded exponential backoff with jitter: base 250ms * 2^attempt + jitter up to 200ms, capped at 10s
        double baseMs = 250.0 * Math.Pow(2, attempt - 1);
        double jitterMs = rng.Next(0, 200);
        double totalMs = Math.Min(10000.0, baseMs + jitterMs);
        return TimeSpan.FromMilliseconds(totalMs);
    }

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? retryAfterHeader)
    {
        if (retryAfterHeader == null) return null;

        if (retryAfterHeader.Delta.HasValue)
        {
            return retryAfterHeader.Delta.Value;
        }

        if (retryAfterHeader.Date.HasValue)
        {
            var diff = retryAfterHeader.Date.Value - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.FromSeconds(1);
        }

        return null;
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri)
        {
            Version = req.Version
        };

        foreach (var header in req.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
