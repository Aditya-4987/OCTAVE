using System;
using System.Collections.Generic;

namespace Octave.Core.Models;

// =================================================================
// 1. EXTERNAL IDENTIFIERS
// =================================================================

/// <summary>
/// Strongly-typed external identifiers for music entities across public music databases.
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
            _ => (AdditionalIds != null && AdditionalIds.TryGetValue(providerKey, out var val)) ? val : null
        };
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
