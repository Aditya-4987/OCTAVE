using System;
using System.Collections.Generic;
using System.IO;
using Octave.Core.Models;

namespace Octave.Core.Services.External;

/// <summary>
/// Reads an <see cref="ExternalIds"/> snapshot out of a TagLib tag (recording MBID /
/// ISRC + additional release-level ids). Shared by the matcher, the enrichment workflow
/// and the smart-library scan so every consumer compares candidates against the SAME
/// view of the local file's identifiers (MATCH-01 / SLE-01: the exact-ID short-circuit
/// used to compare the path-hash <c>Track.Id</c> against MBIDs — a comparison that can
/// never succeed).
/// </summary>
internal static class ExternalTagIds
{
    public static ExternalIds Read(TagLib.Tag? tag)
    {
        if (tag == null) return ExternalIds.Empty;

        var additional = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(tag.MusicBrainzReleaseId))
            additional["MusicBrainzReleaseId"] = tag.MusicBrainzReleaseId.Trim();
        if (!string.IsNullOrWhiteSpace(tag.MusicBrainzArtistId))
            additional["MusicBrainzArtistId"] = tag.MusicBrainzArtistId.Trim();
        if (!string.IsNullOrWhiteSpace(tag.MusicBrainzReleaseGroupId))
            additional["MusicBrainzReleaseGroupId"] = tag.MusicBrainzReleaseGroupId.Trim();

        return new ExternalIds(
            MusicBrainzId: NullIfBlank(tag.MusicBrainzTrackId)?.Trim(),
            Isrc: NullIfBlank(tag.ISRC)?.Trim(),
            AdditionalIds: additional.Count > 0 ? additional : null);
    }

    /// <summary>
    /// Best-effort read for non-local or unreadable files: returns null when the path
    /// doesn't exist or TagLib cannot parse it, so callers fall back to fuzzy scoring.
    /// </summary>
    public static ExternalIds? TryReadFromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            using var tagFile = TagLib.File.Create(path);
            return Read(tagFile.Tag);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExternalTagIds] Tag read failed for '{path}': {ex.Message}");
            return null;
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
