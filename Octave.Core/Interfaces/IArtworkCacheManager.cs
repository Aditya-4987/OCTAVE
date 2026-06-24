using System.Threading.Tasks;

namespace Octave.Core.Interfaces;

public interface IArtworkCacheManager
{
    Task<string?> CacheBytesAsync(byte[] imageData, string? mimeType);
    string CacheRoot { get; }
}
