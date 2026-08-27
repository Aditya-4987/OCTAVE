using System;
using System.Collections.Generic;

namespace Octave.Core.Helpers;

/// <summary>
/// Canonical registry of audio file extensions supported across OCTAVE (BASS engine, TagLib#, scanner, watcher, and DB quality scoring).
/// </summary>
public static class AudioFormatRegistry
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".flac",
        ".m4a",
        ".aac",
        ".ogg",
        ".opus",
        ".wav",
        ".wma",
        ".ape",
        ".wv",
        ".dsf",
        ".dff",
        ".aiff",
        ".aif",
        ".alac",
        ".ac3",
        ".dts",
        ".mpc",
        ".spx",
        ".tta",
        ".ofr",
        ".mp2",
        ".mp1"
    };

    public static bool IsSupported(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return false;
        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }
        return SupportedExtensions.Contains(extension);
    }

    public static IReadOnlySet<string> AllExtensions => SupportedExtensions;
}
