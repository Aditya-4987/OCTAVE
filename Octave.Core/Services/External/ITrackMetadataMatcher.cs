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
        ExternalIds? candidateIds = null,
        // MATCH-01: the local file's real identifiers (MBID/ISRC read from its tags);
        // when null, only fuzzy scoring applies.
        ExternalIds? localIds = null);
}
