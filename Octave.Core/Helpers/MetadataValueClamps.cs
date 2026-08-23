using System;

namespace Octave.Core.Helpers;

// EDITOR-06/ME-04: single source of truth for sane metadata ranges, shared by
// TrackMetadataEditor (user edits) and SmartLibraryEnrichmentService (provider
// candidates), so a nonsensical value can never reach a file tag from either
// path. B16 reuses this class for its own write sites.
public static class MetadataValueClamps
{
    public const int MinYear = 1000;
    public const int MaxTrackNumber = 999;

    public static int MaxYear => DateTime.Now.Year + 1;

    // A year must be plausible: at least 1000, at most next year (pre-releases).
    public static bool IsValidYear(int year) => year >= MinYear && year <= MaxYear;

    // Track/disc numbers are 1-based and bounded by any sane physical release.
    public static bool IsValidTrackNumber(int value) => value >= 1 && value <= MaxTrackNumber;
}
