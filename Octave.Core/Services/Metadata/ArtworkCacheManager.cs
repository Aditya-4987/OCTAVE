using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Interfaces;

namespace Octave.Core.Services.Metadata;

public class ArtworkCacheManager : IArtworkCacheManager
{
    private const string TokenPrefix = "ArtworkCache/";

    // ORC-03: memoized File.Exists probes. A resolve used to hit the
    // synchronous filesystem two or more times per artwork; a short-lived
    // positive/negative memo absorbs the hot re-probe pattern. Races are
    // benign — worst case is a duplicate probe or a stale answer for one
    // lifetime window.
    private sealed class ExistenceProbe
    {
        public bool Exists;
        public long ProbedTicks;
    }

    private static readonly int ExistenceCacheLifetimeMs = 30_000;

    private readonly ConcurrentDictionary<string, ExistenceProbe> _existenceCache = new(StringComparer.OrdinalIgnoreCase);

    public string CacheRoot { get; }

    public ArtworkCacheManager(string cacheRoot)
    {
        CacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));
    }

    public async Task<string?> CacheBytesAsync(byte[] imageData, string? mimeType)
    {
        if (imageData == null || imageData.Length == 0)
            return null;

        // 1. Compute a cryptographic hash string from the raw byte array using SHA-256
        byte[] hashBytes = SHA256.HashData(imageData);
        string sha256Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // 2. Map the mimeType string to a clean file extension (.jpg, .png, .webp, .gif). Default to .jpg if null.
        string extension = ".jpg";
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            if (mimeType.Contains("png", System.StringComparison.OrdinalIgnoreCase))
            {
                extension = ".png";
            }
            else if (mimeType.Contains("webp", System.StringComparison.OrdinalIgnoreCase))
            {
                extension = ".webp";
            }
            else if (mimeType.Contains("gif", System.StringComparison.OrdinalIgnoreCase))
            {
                extension = ".gif";
            }
            else if (mimeType.Contains("jpg", System.StringComparison.OrdinalIgnoreCase) ||
                     mimeType.Contains("jpeg", System.StringComparison.OrdinalIgnoreCase))
            {
                extension = ".jpg";
            }
        }

        // 3. Construct the relative path string: $"ArtworkCache/{sha256Hash}{extension}"
        string relativeToken = $"{TokenPrefix}{sha256Hash}{extension}";

        // 4. Construct the absolute write path: Path.Combine(cacheRoot, $"{sha256Hash}{extension}")
        string absolutePath = Path.Combine(CacheRoot, $"{sha256Hash}{extension}");

        // 5. If the target file already exists on disk, short-circuit and bypass filesystem write pass entirely
        if (!File.Exists(absolutePath))
        {
            // Ensure target directory exists (just in case)
            string? dir = Path.GetDirectoryName(absolutePath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllBytesAsync(absolutePath, imageData);
        }

        // ORC-03: we just confirmed the file is on disk — prime the existence
        // memo so the caller's immediate verification probe doesn't re-hit the
        // filesystem.
        _existenceCache[absolutePath] = new ExistenceProbe
        {
            Exists = true,
            ProbedTicks = Environment.TickCount64
        };

        // 6. Return the relative token: "ArtworkCache/{sha256Hash}{extension}"
        return relativeToken;
    }

    public string ResolveTokenPath(string relativeToken)
    {
        if (string.IsNullOrWhiteSpace(relativeToken))
        {
            throw new ArgumentException("Artwork cache token must not be empty.", nameof(relativeToken));
        }

        // Strip the manager-owned "ArtworkCache/" prefix in either slash flavor;
        // consumers no longer duplicate this layout knowledge (ORC-03/OL-08).
        string relative = relativeToken.StartsWith(TokenPrefix, StringComparison.OrdinalIgnoreCase)
            ? relativeToken[TokenPrefix.Length..]
            : relativeToken;
        relative = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        return Path.Combine(CacheRoot, relative);
    }

    public bool CachedFileExists(string relativeToken)
    {
        string absolutePath = ResolveTokenPath(relativeToken);
        long nowTicks = Environment.TickCount64;

        if (_existenceCache.TryGetValue(absolutePath, out var probe) &&
            nowTicks - Volatile.Read(ref probe.ProbedTicks) < ExistenceCacheLifetimeMs)
        {
            return probe.Exists;
        }

        bool exists = File.Exists(absolutePath);
        _existenceCache[absolutePath] = new ExistenceProbe
        {
            Exists = exists,
            ProbedTicks = nowTicks
        };
        return exists;
    }

    public Task ClearCacheAsync()
    {
        _existenceCache.Clear();
        if (Directory.Exists(CacheRoot))
        {
            try
            {
                var dirInfo = new DirectoryInfo(CacheRoot);
                foreach (var file in dirInfo.EnumerateFiles())
                {
                    try { file.Delete(); } catch { }
                }
                foreach (var dir in dirInfo.EnumerateDirectories())
                {
                    try { dir.Delete(true); } catch { }
                }
            }
            catch { }
        }
        return Task.CompletedTask;
    }
}
