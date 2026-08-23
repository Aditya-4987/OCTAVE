using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Services.Database;

namespace Octave.Core.Services.Cache;

public class TwoTierExternalDataCache : IExternalDataCache
{
    // CACHE-03: last-access tracking turns eviction into true LRU (the old
    // CreatedAt ordering aged out hot entries) and each purge removes a
    // proportional batch so the sort cost amortizes over many inserts instead
    // of running an OrderBy on every set.
    private sealed class L1Entry
    {
        public required object Value { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public long LastAccessTicks;

        public void Touch() => Interlocked.Exchange(ref LastAccessTicks, Environment.TickCount64);
    }

    // CACHE-02: defensive-copy factories for closed List<T> types, built once
    // per payload type. Arrays clone directly; everything else is treated as
    // immutable-by-convention and passes through.
    private static readonly ConcurrentDictionary<Type, Func<object, object>> ListCopiers = new();

    private const double EvictBatchFraction = 0.2;

    private readonly ConcurrentDictionary<string, L1Entry> _l1Cache = new(StringComparer.OrdinalIgnoreCase);
    // CACHE-04: serializes capacity evaluation with insertion — the old
    // EnsureL1Capacity()-then-indexer sequence was check-then-act, letting N
    // concurrent setters all pass the size gate and collectively overshoot.
    private readonly object _l1Gate = new();
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

    // internal for tests (InternalsVisibleTo): direct L1 occupancy probes.
    // GetAsync cannot observe eviction — entries purged from L1 remain served
    // by the L2 tier by design, so tier membership is only visible here.
    internal int L1EntryCount => _l1Cache.Count;
    internal bool IsL1Resident(string key) => _l1Cache.ContainsKey(key);
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var item = await GetWithMetadataAsync<T>(key, allowStale: false, ct).ConfigureAwait(false);
        return item != null ? item.Value : default;
    }

    public async Task<CacheItem<T>?> GetWithMetadataAsync<T>(string key, bool allowStale = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // 1. Check L1 Memory Cache. The stored value is never handed out raw:
        // mutable collection payloads are copied on the way out (CACHE-02), so
        // one consumer's edits can't leak into another consumer's view.
        if (_l1Cache.TryGetValue(key, out var entry))
        {
            bool isExpired = entry.ExpiresAt <= now;
            if (!isExpired || allowStale)
            {
                if (entry.Value is T typedVal)
                {
                    entry.Touch();
                    return new CacheItem<T>(DefensiveCopy(typedVal), entry.CreatedAt, entry.ExpiresAt, isExpired);
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
                        // Promote to L1. The fresh deserialization has no other
                        // referents, so no defensive copy is needed here.
                        InsertIntoL1(key, new L1Entry
                        {
                            Value = deserialized,
                            CreatedAt = createdAt,
                            ExpiresAt = expiresAt,
                            LastAccessTicks = Environment.TickCount64
                        });

                        return new CacheItem<T>(deserialized, createdAt, expiresAt, isExpired);
                    }
                }
                catch (JsonException)
                {
                    // Corrupted row: remove from L2. CACHE-05: the removal is
                    // awaited under the write lock — the old fire-and-forget
                    // raced concurrent writers and could drop a row being
                    // written at that moment.
                    try
                    {
                        await _writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        try
                        {
                            await _dbContext.RemoveCachedExternalDataAsync(key).ConfigureAwait(false);
                        }
                        finally
                        {
                            _writeLock.Release();
                        }
                    }
                    catch (Exception rmEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TwoTierExternalDataCache] Failed to purge corrupt L2 row '{key}': {rmEx.Message}");
                    }
                }
            }
        }

        return null;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key) || value == null) return;

        // CACHE-06: honour cancellation BEFORE mutating any cache tier — a
        // cancelled call used to leave a freshly-written L1 entry behind while
        // skipping only the L2 write.
        if (ct.IsCancellationRequested) return;

        TimeSpan effectiveTtl = ttl ?? _defaultTtl;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = now + effectiveTtl;

        // 1. Populate L1 Memory Cache with a snapshot of the payload (CACHE-02):
        // the master must not alias the caller's live object.
        InsertIntoL1(key, new L1Entry
        {
            Value = DefensiveCopyCore(value),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            LastAccessTicks = Environment.TickCount64
        });

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

    // CACHE-04: capacity evaluation and insertion happen under one lock, making
    // the evict+insert pair atomic. CACHE-03: expired entries go free first,
    // then a proportional LRU batch (oldest last-access first) is purged so the
    // sort cost runs only at the capacity boundary and amortizes over the next
    // ~20% × capacity inserts.
    private void InsertIntoL1(string key, L1Entry entry)
    {
        lock (_l1Gate)
        {
            if (_l1Cache.Count >= _maxL1Capacity)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;

                List<string> expiredKeys = new();
                foreach (var kvp in _l1Cache)
                {
                    if (kvp.Value.ExpiresAt <= now)
                    {
                        expiredKeys.Add(kvp.Key);
                    }
                }
                foreach (string k in expiredKeys)
                {
                    _l1Cache.TryRemove(k, out _);
                }

                if (_l1Cache.Count >= _maxL1Capacity)
                {
                    int victims = Math.Min(
                        _l1Cache.Count,
                        Math.Max(1, (int)Math.Ceiling(_maxL1Capacity * EvictBatchFraction)));
                    foreach (string k in _l1Cache
                        .OrderBy(kvp => kvp.Value.LastAccessTicks)
                        .Take(victims)
                        .Select(kvp => kvp.Key)
                        .ToList())
                    {
                        _l1Cache.TryRemove(k, out _);
                    }
                }
            }

            _l1Cache[key] = entry;
        }
    }

    // CACHE-02: mutable collection wrappers get defensive copies on the way in
    // and out; strings, primitives, and the immutable-by-convention record
    // payloads pass through untouched. Only closed List<> types need the
    // reflective copy ctor, cached once per payload type.
    private static T DefensiveCopy<T>(T value)
    {
        if (value is null) return value;
        return (T)DefensiveCopyCore(value);
    }

    private static object DefensiveCopyCore(object value)
    {
        Type t = value.GetType();
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)) return value;

        if (value is Array arr)
        {
            return arr.Clone();
        }

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            Func<object, object> copier = ListCopiers.GetOrAdd(t, static listType =>
            {
                var elementType = listType.GetGenericArguments()[0];
                var ctor = listType.GetConstructor(new[] { typeof(IEnumerable<>).MakeGenericType(elementType) })!;
                return v => ctor.Invoke(new[] { v });
            });
            return copier(value);
        }

        return value;
    }
}
