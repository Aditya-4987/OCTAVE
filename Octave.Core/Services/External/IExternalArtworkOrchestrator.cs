using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Services.External;

public interface IExternalArtworkOrchestrator
{
    Task<string?> ResolveAndCacheAlbumArtworkAsync(
        string albumTitle,
        string artistName,
        ExternalIds? externalIds = null,
        CancellationToken ct = default);

    Task<string?> ResolveAndCacheArtistImageAsync(
        string artistName,
        ExternalIds? externalIds = null,
        CancellationToken ct = default);
}
