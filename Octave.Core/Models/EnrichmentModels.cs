using System;
using System.Collections.Generic;

namespace Octave.Core.Models;

public enum MatchConfidenceTier
{
    NoMatch,
    AmbiguousMatch,
    ProbableMatch,
    ExactMatch
}

public record FieldComparison<T>(
    string FieldName,
    T? CurrentValue,
    T? ProposedValue,
    bool HasDifference,
    bool IsSelectedByDefault = true
);

public record CandidatePreview(
    string CandidateId,
    string ProviderName,
    double Confidence,
    MatchConfidenceTier ConfidenceTier,
    string MatchEvidence,
    FieldComparison<string> Title,
    FieldComparison<string> Artist,
    FieldComparison<string> Album,
    FieldComparison<string> AlbumArtist,
    FieldComparison<string> Composer,
    FieldComparison<string> Genre,
    FieldComparison<int?> Year,
    FieldComparison<int?> TrackNumber,
    FieldComparison<int?> TrackCount,
    FieldComparison<int?> DiscNumber,
    FieldComparison<int?> DiscCount,
    FieldComparison<string> Lyrics,
    FieldComparison<string> ArtworkUrl,
    FieldComparison<ExternalIds> ExternalIds,
    byte[]? ProposedArtworkBytes = null,
    string? ProposedArtworkMime = null
);

public record TrackEnrichmentPlan(
    Track LocalTrack,
    MatchConfidenceTier TopConfidenceTier,
    IReadOnlyList<CandidatePreview> Candidates,
    CandidatePreview? BestCandidate
);

public record FieldSelectionOptions(
    bool ApplyTitle = true,
    bool ApplyArtist = true,
    bool ApplyAlbum = true,
    bool ApplyAlbumArtist = true,
    bool ApplyComposer = true,
    bool ApplyGenre = true,
    bool ApplyYear = true,
    bool ApplyTrackNumber = true,
    bool ApplyTrackCount = true,
    bool ApplyDiscNumber = true,
    bool ApplyDiscCount = true,
    bool ApplyLyrics = true,
    bool ApplyArtwork = true,
    bool ApplyExternalIds = true
);

public record EnrichmentApplyRequest(
    string TrackId,
    CandidatePreview Candidate,
    FieldSelectionOptions Selection
);

public record EnrichmentApplyResult(
    bool Success,
    MetadataEditResult EditResult,
    IReadOnlyList<string> AppliedFields,
    string? ErrorMessage = null
);
