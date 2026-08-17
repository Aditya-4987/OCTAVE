using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Octave.Core.Helpers;

public static class IdGenerator
{
    // Deterministic IDs

    public static string FromArtist(string artistName) =>
        GenerateDeterministicGuid(
            $"artist:{artistName.Trim().ToLowerInvariant()}");

    public static string FromAlbum(string artistName, string albumTitle) =>
        GenerateDeterministicGuid(
            $"album:{artistName.Trim().ToLowerInvariant()}||{albumTitle.Trim().ToLowerInvariant()}");

    public static string FromTrackUri(string absoluteUriOrPath)
    {
        string canonical = absoluteUriOrPath;
        try
        {
            if (!absoluteUriOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !absoluteUriOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                canonical = Path.GetFullPath(absoluteUriOrPath);
            }
        }
        catch
        {
            // Fallback if path string cannot be resolved by GetFullPath
        }
        canonical = canonical.Replace('/', Path.DirectorySeparatorChar)
                             .Replace('\\', Path.DirectorySeparatorChar)
                             .Trim()
                             .ToLowerInvariant();
        return GenerateDeterministicGuid($"track:{canonical}");
    }

    private static string GenerateDeterministicGuid(string input)
    {
        byte[] inputBytes = Encoding.UTF8.GetBytes(input);
        byte[] hashBytes = SHA256.HashData(inputBytes);

        return new Guid(hashBytes.AsSpan(0, 16)).ToString();
    }

    // File date resolution

    public static DateTime ResolveFileDateAdded(string filePath)
    {
        try
        {
            DateTime dt = File.GetCreationTimeUtc(filePath);

            if (dt.Year < 1980)
            {
                dt = File.GetLastWriteTimeUtc(filePath);
            }

            return dt.Year < 1980
                ? DateTime.UtcNow
                : dt;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IdGenerator] Failed to resolve file dates for '{filePath}': {ex.Message}");
            return DateTime.UtcNow;
        }
    }
}