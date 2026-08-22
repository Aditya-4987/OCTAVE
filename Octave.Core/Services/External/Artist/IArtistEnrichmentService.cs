using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Services.External.Artist;

public interface IArtistEnrichmentService
{
    Task<EnrichedArtistProfile?> GetEnrichedArtistAsync(
        string artistName,
        string? musicBrainzArtistId = null,
        CancellationToken ct = default);
}
