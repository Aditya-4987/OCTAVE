using System;
using System.Threading;
using System.Threading.Tasks;

namespace Octave.Core.Services.Network;

public interface IProviderRateLimiter
{
    string ProviderKey { get; }
    TimeSpan MinInterval { get; }
    Task WaitAsync(CancellationToken ct = default);
    void NotifyRetryAfter(TimeSpan retryAfter);
}

public interface IProviderRateLimiterRegistry
{
    IProviderRateLimiter GetOrCreate(string providerKey, TimeSpan? defaultMinInterval = null);
}
