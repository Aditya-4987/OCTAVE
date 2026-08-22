using System;
using System.Collections.Generic;

namespace Octave.Core.Models;

// =================================================================
// 1. EXTERNAL IDENTIFIERS
// =================================================================

/// <summary>
/// Strongly-typed external identifiers for music entities across public music databases.
/// Value equality is implemented manually (AR-03/ENR-01): the generated record equality
/// would compare <see cref="AdditionalIds"/> dictionaries BY REFERENCE, so two logically
/// identical snapshots never compared equal — flagging External IDs as perpetually
/// "different" in enrichment diffs and breaking any dictionary/set usage.
/// Scalars and dictionary VALUES use Ordinal (identifiers are case-sensitive);
/// dictionary KEYS are OrdinalIgnoreCase (key casing is purely conventional).
/// </summary>
public record ExternalIds(
    string? MusicBrainzId = null,
    string? SpotifyId = null,
    string? DiscogsId = null,
    string? Isrc = null,
    string? AcoustId = null,
    IReadOnlyDictionary<string, string>? AdditionalIds = null
)
{
    public static readonly ExternalIds Empty = new();

    public virtual bool Equals(ExternalIds? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return string.Equals(MusicBrainzId, other.MusicBrainzId, StringComparison.Ordinal)
            && string.Equals(SpotifyId, other.SpotifyId, StringComparison.Ordinal)
            && string.Equals(DiscogsId, other.DiscogsId, StringComparison.Ordinal)
            && string.Equals(Isrc, other.Isrc, StringComparison.Ordinal)
            && string.Equals(AcoustId, other.AcoustId, StringComparison.Ordinal)
            && AdditionalIdsEquals(AdditionalIds, other.AdditionalIds);
    }

    public override int GetHashCode()
    {
        // XOR-fold the dictionary entries so entry ORDER doesn't affect the hash;
        // keys are upper-cased to match the OrdinalIgnoreCase key comparison in Equals.
        int hash = HashCode.Combine(MusicBrainzId, SpotifyId, DiscogsId, Isrc, AcoustId);
        if (AdditionalIds != null)
        {
            foreach (var kvp in AdditionalIds)
            {
                hash ^= HashCode.Combine(kvp.Key?.ToUpperInvariant(), kvp.Value);
            }
        }
        return hash;
    }

    private static bool AdditionalIdsEquals(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        if (left.Count != right.Count) return false;

        foreach (var kvp in left)
        {
            if (!TryGetById(right, kvp.Key, out var rightValue)) return false;
            if (!string.Equals(kvp.Value, rightValue, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public string? GetId(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return null;

        return providerKey.ToLowerInvariant() switch
        {
            "musicbrainz" or "mbid" => MusicBrainzId,
            "spotify" => SpotifyId,
            "discogs" => DiscogsId,
            "isrc" => Isrc,
            "acoustid" => AcoustId,
            // AR-03: the switch above is case-insensitive, so the dictionary fallback
            // must be too — a stored "musicbrainzreleaseid" key used to miss a
            // "MusicBrainzReleaseId" lookup and vice versa.
            _ => TryGetById(AdditionalIds, providerKey, out var val) ? val : null
        };
    }

    private static bool TryGetById(IReadOnlyDictionary<string, string>? source, string key, out string? value)
    {
        if (source != null)
        {
            if (source.TryGetValue(key, out var exact))
            {
                value = exact;
                return true;
            }

            foreach (var kvp in source)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }
}

// =================================================================
// 2. PURE EXTERNAL METADATA MODELS (Unopinionated data representation)
// =================================================================

public record ExternalTrackMetadata(
    string Title,
    string ArtistName,
    string? AlbumTitle,
    int? Year,
    string? Genre,
    int? TrackNumber,
    int? DiscNumber,
    double? DurationSeconds,
    string? Isrc,
    ExternalIds ExternalIds
);

public record ExternalAlbumMetadata(
    string Title,
    string ArtistName,
    int? Year,
    string? ReleaseDate,
    string? Genre,
    int? TotalTracks,
    ExternalIds ExternalIds,
    IReadOnlyList<ExternalTrackMetadata>? Tracklist = null
);

public record ExternalArtistMetadata(
    string Name,
    string? Bio,
    string? Country,
    int? FormedYear,
    int? DisbandedYear,
    ExternalIds ExternalIds
);

// =================================================================
// 3. MATCH CANDIDATES (Ranked suggestions with confidence & evidence)
// =================================================================

public record TrackMatchCandidate(
    string ProviderName,
    ExternalIds ExternalIds,
    double Confidence,
    string MatchEvidence,
    ExternalTrackMetadata Metadata
);

public record AlbumMatchCandidate(
    string ProviderName,
    ExternalIds ExternalIds,
    double Confidence,
    string MatchEvidence,
    ExternalAlbumMetadata Metadata
);

public record ArtistMatchCandidate(
    string ProviderName,
    ExternalIds ExternalIds,
    double Confidence,
    string MatchEvidence,
    ExternalArtistMetadata Metadata
);

// =================================================================
// 4. EXTERNAL LYRICS RESULT
// =================================================================

public record ExternalLyricsResult(
    string? TrackId,
    LyricsState State,
    IReadOnlyList<LyricLine>? SyncedLines,
    string? PlainText,
    string ProviderName,
    ExternalIds? ExternalIds = null
);

// =================================================================
// 5. LOCAL METADATA EDITING MODELS (Multi-Stage Result Verification)
// =================================================================

public record TrackMetadataUpdate(
    string? Title = null,
    string? ArtistName = null,
    string? AlbumTitle = null,
    string? AlbumArtist = null,
    string? Composer = null,
    string? Genre = null,
    int? Year = null,
    int? TrackNumber = null,
    int? TrackCount = null,
    int? DiscNumber = null,
    int? DiscCount = null,
    string? Comment = null,
    string? Lyrics = null,
    byte[]? NewArtworkBytes = null,
    string? ArtworkMimeType = null,
    bool ClearArtwork = false,
    ExternalIds? ExternalIds = null,
    float? ReplayGain = null
);

public record FileWriteResult(
    bool Success,
    string? ErrorMessage,
    string FilePath
)
{
    public static FileWriteResult Succeeded(string filePath) => new(true, null, filePath);
    public static FileWriteResult Failed(string filePath, string errorMessage) => new(false, errorMessage, filePath);
}

public record DbSyncResult(
    bool Success,
    string? ErrorMessage,
    Track? UpdatedTrack
)
{
    public static DbSyncResult Succeeded(Track updatedTrack) => new(true, null, updatedTrack);
    public static DbSyncResult Failed(string errorMessage) => new(false, errorMessage, null);
}

public record MetadataEditResult(
    bool Success,
    FileWriteResult FileResult,
    DbSyncResult? DbResult,
    string? SummaryMessage
)
{
    public static MetadataEditResult Succeeded(FileWriteResult fileRes, DbSyncResult dbRes) =>
        new(true, fileRes, dbRes, "Track metadata updated on disk and database synchronized.");

    public static MetadataEditResult FileFailed(FileWriteResult fileRes) =>
        new(false, fileRes, null, $"File write failed: {fileRes.ErrorMessage}");

    public static MetadataEditResult DbSyncFailed(FileWriteResult fileRes, DbSyncResult dbRes) =>
        new(false, fileRes, dbRes, $"File written successfully, but database sync failed: {dbRes.ErrorMessage}");
}
