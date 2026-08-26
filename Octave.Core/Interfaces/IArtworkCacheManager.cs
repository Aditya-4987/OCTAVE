using System.Threading.Tasks;

namespace Octave.Core.Interfaces;

public interface IArtworkCacheManager
{
    Task<string?> CacheBytesAsync(byte[] imageData, string? mimeType);
    string CacheRoot { get; }

    // ORC-03/OL-08: the token layout ("ArtworkCache/{hash}{ext}") is owned by
    // this manager — consumers previously duplicated the prefix-strip with a
    // fragile string Replace. Resolve it centrally instead.
    string ResolveTokenPath(string relativeToken);

    /// <summary>
    /// True when the token's backing file exists. Probes are briefly memoized
    /// (ORC-03) so hot paths stop hitting synchronous File.Exists several times
    /// per resolve; writes through CacheBytesAsync prime the cache positive.
    /// </summary>
    bool CachedFileExists(string relativeToken);

    /// <summary>
    /// Purges all cached artwork files from disk and clears the in-memory probe cache.
    /// </summary>
    Task ClearCacheAsync();
}
