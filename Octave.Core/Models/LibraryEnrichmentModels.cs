using System;
using System.Collections.Generic;
using Octave.Core.Models;

namespace Octave.Core.Models;

public enum EnrichmentTrackStatus
{
    Pending = 0,
    SafeReadyToApply = 1,
    NeedsReview = 2,
    NoMatch = 3,
    AlreadyComplete = 4,
    EnrichedSuccessfully = 5,
    Skipped = 6,
    Failed = 7,
    NeverAskAgain = 8
}

[Flags]
public enum EnrichmentActions
{
    None = 0,
    WriteTitle = 1 << 0,
    WriteArtist = 1 << 1,
    WriteAlbum = 1 << 2,
    WriteAlbumArtist = 1 << 3,
    WriteComposer = 1 << 4,
    WriteGenre = 1 << 5,
    WriteYear = 1 << 6,
    WriteTrackNumber = 1 << 7,
    WriteDiscNumber = 1 << 8,
    WriteExternalIds = 1 << 9,
    WriteLyrics = 1 << 10,
    WriteArtwork = 1 << 11,
    EnrichArtistBioAndPhoto = 1 << 12
}

public record TrackCompleteness(
    bool IsMetadataComplete,
    bool IsArtworkComplete,
    bool IsArtistComplete,
    bool IsLyricsComplete)
{
    public bool IsFullyComplete => IsMetadataComplete && IsArtworkComplete && IsArtistComplete && IsLyricsComplete;
}

public record SafetyGateResult(
    bool Passed,
    bool TitlePassed,
    bool ArtistPassed,
    bool DurationPassed,
    bool VersionCompatible,
    IReadOnlyList<string> PositiveEvidence,
    IReadOnlyList<string> Warnings);

public class TrackEnrichmentExecutionPlan
{
    public string TrackId { get; set; } = string.Empty;
    public string TrackUri { get; set; } = string.Empty;
    public string LocalTitle { get; set; } = string.Empty;
    public string LocalArtist { get; set; } = string.Empty;
    public string LocalAlbum { get; set; } = string.Empty;
    public double LocalDurationSeconds { get; set; }
    public int LocalYear { get; set; }
    public int LocalTrackNumber { get; set; }

    public TrackMatchCandidate? SelectedCandidate { get; set; }
    public IReadOnlyList<TrackMatchCandidate> AlternativeCandidates { get; set; } = Array.Empty<TrackMatchCandidate>();

    public double Confidence { get; set; }
    public SafetyGateResult SafetyGates { get; set; } = new(false, false, false, false, false, Array.Empty<string>(), Array.Empty<string>());
    public EnrichmentTrackStatus Status { get; set; } = EnrichmentTrackStatus.Pending;
    public EnrichmentActions PlannedActions { get; set; } = EnrichmentActions.None;

    public TrackMetadataUpdate? ProposedUpdate { get; set; }
    public string? ErrorMessage { get; set; }

    public bool HasPlannedWrites => PlannedActions != EnrichmentActions.None;
}

public record EnrichmentSessionInfo(
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool IsDryRun,
    int TotalTracks);

public record LibraryEnrichmentProgress(
    string SessionId,
    int TotalTracks,
    int ScannedTracks,
    int SafeReadyCount,
    int EnrichedCount,
    int AlreadyCompleteCount,
    int NeedsReviewCount,
    int NoMatchCount,
    int FailedCount,
    string CurrentTrackTitle,
    string CurrentOperation,
    double Percentage,
    bool IsRunning,
    bool IsCancelled);

public record LibraryEnrichmentSummary(
    string SessionId,
    bool IsDryRun,
    int TotalTracks,
    int ScannedTracks,
    int SafeReadyCount,
    int EnrichedCount,
    int AlreadyCompleteCount,
    int NeedsReviewCount,
    int NoMatchCount,
    int FailedCount,
    int MetadataFieldsUpdated,
    int AlbumArtworkAdded,
    int ArtistEnrichmentsAdded,
    int LyricsAdded,
    TimeSpan ElapsedDuration,
    IReadOnlyList<TrackEnrichmentExecutionPlan> ExecutionPlans);
