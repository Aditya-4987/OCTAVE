using System;
using System.IO;

namespace Octave.Core.Helpers;

// NF-31: reads pixel dimensions straight from JPEG (SOF markers) / PNG (IHDR)
// headers. Used to decide whether an already-cached artwork file is worth
// upgrading - spinning up a full decoder for a width probe would pull a UI
// framework dependency into Core.
public static class ImageDimensionReader
{
    /// <summary>
    /// Returns the encoded pixel width of the image at <paramref name="path"/>,
    /// or null when the format is unsupported / the file is unreadable / the
    /// headers are malformed. Never throws.
    /// </summary>
    public static int? TryReadWidth(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            if (stream.Read(header) < 8)
            {
                return null;
            }

            // PNG: 8-byte signature then IHDR chunk - width at fixed offset 16.
            if (header[0] == 0x89 && header[1] == 0x50)
            {
                Span<byte> ihdr = stackalloc byte[4];
                stream.Position = 16;
                if (stream.Read(ihdr) < 4)
                {
                    return null;
                }
                int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(ihdr);
                return width > 0 ? width : null;
            }

            // JPEG: FF D8 start; scan segment markers for an SOFn frame header.
            if (header[0] == 0xFF && header[1] == 0xD8)
            {
                stream.Position = 2;
                return ScanJpegForFrameWidth(stream);
            }
        }
        catch
        {
            // Probe only - any failure simply means "unknown width".
        }

        return null;
    }

    private static int? ScanJpegForFrameWidth(Stream stream)
    {
        Span<byte> marker = stackalloc byte[4];

        while (stream.Read(marker.Slice(0, 2)) == 2)
        {
            if (marker[0] != 0xFF)
            {
                return null; // desynced - not walking out of garbage
            }

            byte markerType = marker[1];
            if (markerType == 0xD8 || (markerType >= 0xD0 && markerType <= 0xD7))
            {
                continue; // standalone markers carry no length
            }
            if (markerType == 0xFF)
            {
                stream.Position -= 1; // fill byte before the real marker
                continue;
            }
            if (stream.Read(marker.Slice(2, 2)) != 2)
            {
                return null;
            }
            int segmentLength = (marker[2] << 8) | marker[3];

            // SOF0-SOF15 except DHT(C4)/DAC(CC)/RST/RST-end are frame headers.
            if (markerType >= 0xC0 && markerType <= 0xCF &&
                markerType != 0xC4 && markerType != 0xC8 && markerType != 0xCC)
            {
                Span<byte> sofTail = stackalloc byte[5];
                if (stream.Read(sofTail) < 5)
                {
                    return null;
                }
                // height(2) width(2), big-endian, after precision+lines.
                return (sofTail[3] << 8) | sofTail[4];
            }

            stream.Position += segmentLength - 2;
        }

        return null;
    }
}
