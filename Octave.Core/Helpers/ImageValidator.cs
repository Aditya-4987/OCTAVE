using System;

namespace Octave.Core.Helpers;

public static class ImageValidator
{
    public const int MinImageSizeBytes = 8;
    public const int MaxImageSizeBytes = 20 * 1024 * 1024; // 20 MB

    private static readonly byte[] JpegHeader = new byte[] { 0xFF, 0xD8, 0xFF };
    private static readonly byte[] PngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] Gif87Header = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }; // GIF87a
    private static readonly byte[] Gif89Header = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }; // GIF89a
    private static readonly byte[] RiffHeader = new byte[] { 0x52, 0x49, 0x46, 0x46 }; // RIFF
    private static readonly byte[] WebpHeader = new byte[] { 0x57, 0x45, 0x42, 0x50 }; // WEBP

    /// <summary>
    /// Validates raw bytes against known image magic headers and size constraints.
    /// </summary>
    public static bool IsValidImage(byte[]? data, out string detectedMimeType)
    {
        detectedMimeType = "image/jpeg";

        if (data == null || data.Length < MinImageSizeBytes || data.Length > MaxImageSizeBytes)
        {
            return false;
        }

        // 1. JPEG Check (FF D8 FF)
        if (data.Length >= 3 && data[0] == JpegHeader[0] && data[1] == JpegHeader[1] && data[2] == JpegHeader[2])
        {
            detectedMimeType = "image/jpeg";
            return true;
        }

        // 2. PNG Check (89 50 4E 47 0D 0A 1A 0A)
        if (data.Length >= 8 && MatchesHeader(data, PngHeader))
        {
            detectedMimeType = "image/png";
            return true;
        }

        // 3. WebP Check (RIFF....WEBP)
        if (data.Length >= 12 && MatchesHeader(data, RiffHeader) &&
            data[8] == WebpHeader[0] && data[9] == WebpHeader[1] && data[10] == WebpHeader[2] && data[11] == WebpHeader[3])
        {
            detectedMimeType = "image/webp";
            return true;
        }

        // 4. GIF Check
        if (data.Length >= 6 && (MatchesHeader(data, Gif87Header) || MatchesHeader(data, Gif89Header)))
        {
            detectedMimeType = "image/gif";
            return true;
        }

        return false;
    }

    private static bool MatchesHeader(byte[] data, byte[] header)
    {
        for (int i = 0; i < header.Length; i++)
        {
            if (data[i] != header[i]) return false;
        }
        return true;
    }
}
