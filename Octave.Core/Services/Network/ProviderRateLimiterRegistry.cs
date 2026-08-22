using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Octave.Core.Services.Network;

public class ProviderRateLimiter : IProviderRateLimiter
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private DateTimeOffset _nextAllowedTime = DateTimeOffset.MinValue;
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
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_nextAllowedTime > now)
            {
                TimeSpan delay = _nextAllowedTime - now;
                await Task.Delay(delay, ct).ConfigureAwait(false);
                now = DateTimeOffset.UtcNow;
            }

            _nextAllowedTime = now + _minInterval;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void NotifyRetryAfter(TimeSpan retryAfter)
    {
        DateTimeOffset target = DateTimeOffset.UtcNow + retryAfter;
        // Extend next allowed time if retryAfter is greater
        if (target > _nextAllowedTime)
        {
            _nextAllowedTime = target;
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

    public IProviderRateLimiter GetOrCreate(string providerKey, TimeSpan? defaultMinInterval = null)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            providerKey = "default";

        return _limiters.GetOrAdd(providerKey, key =>
            new ProviderRateLimiter(key, defaultMinInterval ?? _defaultInterval));
    }
}
