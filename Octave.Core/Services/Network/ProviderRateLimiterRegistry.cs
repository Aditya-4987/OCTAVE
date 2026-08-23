using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Octave.Core.Services.Network;

public class ProviderRateLimiter : IProviderRateLimiter
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    // NET-01: the next-allowed instant is stored as UTC ticks in a single long
    // and accessed with Interlocked. The old raw DateTimeOffset field (16 bytes)
    // could tear when NotifyRetryAfter's unsynchronized read/write raced a
    // semaphore-holder's write inside WaitAsync.
    private long _nextAllowedTicksUtc;
    private readonly TimeSpan _minInterval;

    public string ProviderKey { get; }
    public TimeSpan MinInterval => _minInterval;

    public ProviderRateLimiter(string providerKey, TimeSpan minInterval)
    {
        ProviderKey = providerKey ?? throw new ArgumentNullException(nameof(providerKey));
        _minInterval = minInterval;
    }

    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            if (Interlocked.Read(ref _nextAllowedTicksUtc) > nowTicks)
            {
                TimeSpan delay = TimeSpan.FromTicks(Interlocked.Read(ref _nextAllowedTicksUtc) - nowTicks);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                nowTicks = DateTime.UtcNow.Ticks;
            }

            Interlocked.Exchange(ref _nextAllowedTicksUtc, nowTicks + _minInterval.Ticks);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void NotifyRetryAfter(TimeSpan retryAfter)
    {
        long target = DateTime.UtcNow.Ticks + retryAfter.Ticks;
        long current = Interlocked.Read(ref _nextAllowedTicksUtc);

        // Extend the next allowed time only — never shorten an in-flight
        // backoff window. CAS loop keeps the read/extend atomic against the
        // semaphore holder's writes.
        while (target > current)
        {
            long previous = Interlocked.CompareExchange(ref _nextAllowedTicksUtc, target, current);
            if (previous == current) break;
            current = previous;
        }
    }
}

public class ProviderRateLimiterRegistry : IProviderRateLimiterRegistry
{
    private readonly ConcurrentDictionary<string, IProviderRateLimiter> _limiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _defaultInterval;

    public ProviderRateLimiterRegistry(TimeSpan? defaultInterval = null)
    {
        _defaultInterval = defaultInterval ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>
    /// Returns the limiter for a provider, creating it on first use.
    /// NET-04: the interval binds at FIRST creation for a key — later callers
    /// passing a different <paramref name="defaultMinInterval"/> share the
    /// original instance (per-key spacing state must survive across calls;
    /// re-creating would reset it and permit bursts). Stricter per-provider
    /// floors that must not depend on construction order are enforced above
    /// this layer (see HttpService's PROV-02 decorator).
    /// </summary>
    public IProviderRateLimiter GetOrCreate(string providerKey, TimeSpan? defaultMinInterval = null)
    {
        string key = string.IsNullOrWhiteSpace(providerKey) ? "default" : providerKey;
        TimeSpan interval = defaultMinInterval ?? _defaultInterval;

        // Keyed GetOrAdd overload: no closure allocation per call.
        return _limiters.GetOrAdd(key, static (k, i) => new ProviderRateLimiter(k, i), interval);
    }
}
