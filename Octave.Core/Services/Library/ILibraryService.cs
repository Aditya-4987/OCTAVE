using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Services.Library;

public interface ILibraryService
{
    Task ScanLocalLibraryAsync(string rootDir, CancellationToken ct);

    Task<List<Track>> GetAllTracksAsync();

    Task<List<Artist>> GetAllArtistsAsync();

    Task<List<Album>> GetAllAlbumsAsync();

    Task<List<Track>> GetTracksByAlbumAsync(string albumId);

    Task<int> GetTotalTrackCountAsync();
}
