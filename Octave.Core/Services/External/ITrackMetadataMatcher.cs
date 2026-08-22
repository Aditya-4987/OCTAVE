using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Services.External;

public record MatchScoreResult(
    double Confidence,
    string MatchEvidence,
    bool IsExactIdMatch,
    bool IsVersionMismatch
);

public interface ITrackMetadataMatcher
{
    Task<IReadOnlyList<TrackMatchCandidate>> FindMatchesForTrackAsync(
        Track track,
        CancellationToken ct = default);

    Task<IReadOnlyList<TrackMatchCandidate>> FindMatchesForFileAsync(
        string filePath,
        CancellationToken ct = default);

    MatchScoreResult ScoreCandidate(
        Track localTrack,
        ExternalTrackMetadata candidate,
        string providerName,
        ExternalIds? candidateIds = null);
}
