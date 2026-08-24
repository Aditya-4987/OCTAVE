using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Services.External;

public interface IExternalArtworkOrchestrator
{
    // NF-31: preferHighResolutionUpgrade lets a caller (the smart-enrichment
    // scan) re-sweep providers when the cached file is below the hi-res bar -
    // pre-fix caches are full of 500×500 CAA thumbnails. Default false keeps
    // the plain "cache hit = no network" contract for playback-time callers.
    Task<string?> ResolveAndCacheAlbumArtworkAsync(
        string albumTitle,
        string artistName,
        ExternalIds? externalIds = null,
        CancellationToken ct = default,
        bool preferHighResolutionUpgrade = false);

    Task<string?> ResolveAndCacheArtistImageAsync(
        string artistName,
        ExternalIds? externalIds = null,
        CancellationToken ct = default);
}
