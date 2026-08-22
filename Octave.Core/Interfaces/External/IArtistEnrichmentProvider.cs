using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Interfaces.External;

public interface IArtistEnrichmentProvider
{
    string ProviderName { get; }
    bool IsEnabled { get; }
    int Priority { get; }

    Task<EnrichedArtistProfile?> GetArtistProfileByMbidAsync(
        string musicBrainzArtistId,
        CancellationToken ct = default);

    Task<EnrichedArtistProfile?> GetArtistProfileByNameAsync(
        string artistName,
        CancellationToken ct = default);
}
