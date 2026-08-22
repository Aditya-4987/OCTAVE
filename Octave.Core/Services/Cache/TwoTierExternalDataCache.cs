using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Services.Database;

namespace Octave.Core.Services.Cache;

public class TwoTierExternalDataCache : IExternalDataCache
{
    private record L1Entry(object Value, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, L1Entry> _l1Cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SqliteDbContext _dbContext;
    private readonly TimeSpan _defaultTtl;
    private readonly int _maxL1Capacity;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TwoTierExternalDataCache(
        SqliteDbContext dbContext,
        TimeSpan? defaultTtl = null,
        int maxL1Capacity = 1000)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _defaultTtl = defaultTtl ?? TimeSpan.FromDays(7);
        _maxL1Capacity = maxL1Capacity;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var item = await GetWithMetadataAsync<T>(key, allowStale: false, ct).ConfigureAwait(false);
        return item != null ? item.Value : default;
    }

    public async Task<CacheItem<T>?> GetWithMetadataAsync<T>(string key, bool allowStale = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // 1. Check L1 Memory Cache
        if (_l1Cache.TryGetValue(key, out var entry))
        {
            bool isExpired = entry.ExpiresAt <= now;
            if (!isExpired || allowStale)
            {
                if (entry.Value is T typedVal)
                {
                    return new CacheItem<T>(typedVal, entry.CreatedAt, entry.ExpiresAt, isExpired);
                }
            }
        }

        // 2. Check L2 Persistent SQLite Cache. A DB failure here (e.g. SQLITE_BUSY
        // before busy_timeout existed, a missing/corrupt table) must degrade to a
        // cache miss — letting it throw defeats the orchestrators' offline/stale
        // fallback paths (CACHE-01).
        (string DataJson, string TypeName, long CreatedAt, long ExpiresAt)? record = null;
        try
        {
            record = await _dbContext.GetCachedExternalDataRecordAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TwoTierExternalDataCache] L2 read failed for '{key}', treating as miss: {ex.Message}");
        }

        if (record.HasValue)
        {
            var (dataJson, typeName, createdEpoch, expiresEpoch) = record.Value;
            DateTimeOffset createdAt = DateTimeOffset.FromUnixTimeMilliseconds(createdEpoch);
            DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiresEpoch);
            bool isExpired = expiresAt <= now;

            if (!isExpired || allowStale)
            {
                try
                {
                    var deserialized = JsonSerializer.Deserialize<T>(dataJson, JsonOptions);
                    if (deserialized != null)
                    {
                        // Store in L1 cache with its original expiration
                        EnsureL1Capacity();
                        _l1Cache[key] = new L1Entry(deserialized, createdAt, expiresAt);

                        return new CacheItem<T>(deserialized, createdAt, expiresAt, isExpired);
                    }
                }
                catch (JsonException)
                {
                    // Corrupted row: remove from L2
                    _ = _dbContext.RemoveCachedExternalDataAsync(key);
                }
            }
        }

        return null;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key) || value == null) return;

        TimeSpan effectiveTtl = ttl ?? _defaultTtl;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = now + effectiveTtl;

        // 1. Populate L1 Memory Cache
        EnsureL1Capacity();
        _l1Cache[key] = new L1Entry(value, now, expiresAt);

        if (ct.IsCancellationRequested) return;

        // 2. Persist to L2 SQLite Cache under lock
        string json = JsonSerializer.Serialize(value, JsonOptions);
        string typeName = typeof(T).Name;
        long createdEpoch = now.ToUnixTimeMilliseconds();
        long expiresEpoch = expiresAt.ToUnixTimeMilliseconds();

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _dbContext.SetCachedExternalDataAsync(key, json, typeName, createdEpoch, expiresEpoch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TwoTierExternalDataCache] Failed to write L2 cache for key '{key}': {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        _l1Cache.TryRemove(key, out _);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _dbContext.RemoveCachedExternalDataAsync(key).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ClearExpiredAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Prune expired L1
        foreach (var kvp in _l1Cache)
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                _l1Cache.TryRemove(kvp.Key, out _);
            }
        }

        // Prune expired L2
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _dbContext.ClearExpiredCachedExternalDataAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        _l1Cache.Clear();

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _dbContext.ClearAllCachedExternalDataAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void EnsureL1Capacity()
    {
        if (_l1Cache.Count >= _maxL1Capacity)
        {
            // Evict expired first, then oldest 20%
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var expiredKeys = _l1Cache.Where(kvp => kvp.Value.ExpiresAt <= now).Select(kvp => kvp.Key).Take(100).ToList();
            foreach (var k in expiredKeys)
            {
                _l1Cache.TryRemove(k, out _);
            }

            if (_l1Cache.Count >= _maxL1Capacity)
            {
                var oldestKeys = _l1Cache.OrderBy(kvp => kvp.Value.CreatedAt).Select(kvp => kvp.Key).Take(50).ToList();
                foreach (var k in oldestKeys)
                {
                    _l1Cache.TryRemove(k, out _);
                }
            }
        }
    }
}
