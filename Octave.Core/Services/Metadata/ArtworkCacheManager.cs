using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Octave.Core.Interfaces;

namespace Octave.Core.Services.Metadata;

public class ArtworkCacheManager : IArtworkCacheManager
{
    public string CacheRoot { get; }

    public ArtworkCacheManager(string cacheRoot)
    {
        CacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));
    }

    public async Task<string?> CacheBytesAsync(byte[] imageData, string? mimeType)
    {
        if (imageData == null || imageData.Length == 0)
            return null;

        // 1. Compute a cryptographic hash string from the raw byte array using SHA-256 exclusively
        using var sha256 = SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(imageData);
        string sha256Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // 2. Map the mimeType string to a clean file extension (.jpg, .png). Default to .jpg if null.
        string extension = ".jpg";
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            if (mimeType.Contains("png", System.StringComparison.OrdinalIgnoreCase))
            {
                extension = ".png";
            }
            else if (mimeType.Contains("jpg", System.StringComparison.OrdinalIgnoreCase) || 
                     mimeType.Contains("jpeg", System.StringComparison.OrdinalIgnoreCase))
            {
                extension = ".jpg";
            }
        }

        // 3. Construct the relative path string: $"ArtworkCache/{sha256Hash}{extension}"
        string relativeToken = $"ArtworkCache/{sha256Hash}{extension}";

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

        // 6. Return the relative token: "ArtworkCache/{sha256Hash}{extension}"
        return relativeToken;
    }
}
