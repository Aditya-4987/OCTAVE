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

    public static string FromTrackUri(string absoluteUriOrPath) =>
        GenerateDeterministicGuid(
            $"track:{absoluteUriOrPath.Trim().ToLowerInvariant()}");

    private static string GenerateDeterministicGuid(string input)
    {
        byte[] inputBytes = Encoding.UTF8.GetBytes(input);
        byte[] hashBytes = MD5.HashData(inputBytes);

        return new Guid(hashBytes).ToString();
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
        catch
        {
            return DateTime.UtcNow;
        }
    }
}