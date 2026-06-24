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

    Task<List<Track>> GetTracksByArtistAsync(string artistId);

    Task<Album?> GetAlbumByIdAsync(string albumId);

    Task<Artist?> GetArtistByIdAsync(string artistId);

    Task<int> GetTotalTrackCountAsync();

    Task<Track?> GetTrackByIdAsync(string trackId);

    Task<SearchResults> SearchLibraryAsync(string query, int? limit = null);
}
