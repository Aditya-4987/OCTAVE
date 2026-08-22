using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Interfaces.External;

public interface IExternalMetadataProvider
{
    string ProviderName { get; }
    bool IsEnabled { get; }
    int Priority { get; } // Lower value = higher priority

    Task<IReadOnlyList<TrackMatchCandidate>> SearchTrackCandidatesAsync(
        string title,
        string artist,
        string? album = null,
        double? durationSeconds = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<AlbumMatchCandidate>> SearchAlbumCandidatesAsync(
        string albumTitle,
        string artistName,
        int? year = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ArtistMatchCandidate>> SearchArtistCandidatesAsync(
        string artistName,
        CancellationToken ct = default);

    Task<ExternalTrackMetadata?> GetTrackMetadataAsync(
        string providerEntityId,
        CancellationToken ct = default);

    Task<ExternalAlbumMetadata?> GetAlbumMetadataAsync(
        string providerEntityId,
        CancellationToken ct = default);

    Task<ExternalArtistMetadata?> GetArtistMetadataAsync(
        string providerEntityId,
        CancellationToken ct = default);
}
