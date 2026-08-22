using System;
using System.Threading;
using System.Threading.Tasks;

namespace Octave.Core.Services.Cache;

public record CacheItem<T>(
    T Value,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool IsExpired
);

public interface IExternalDataCache
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task<CacheItem<T>?> GetWithMetadataAsync<T>(string key, bool allowStale = false, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task ClearExpiredAsync(CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
}
