using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Services.Library;

public interface ILibraryService
{
    event System.EventHandler? LibraryUpdated;
    event System.EventHandler? FavoritesChanged;
    Task ScanLocalLibraryAsync(string rootDir, CancellationToken ct);

    // Home dashboards & favorites.
    Task<List<Track>> GetRecentlyPlayedAsync(int limit);
    Task<List<Track>> GetMostPlayedAsync(int limit);
    Task<List<Track>> GetLastAddedAsync(int limit);
    Task<List<Track>> GetFavoritesAsync();
    Task<System.Collections.Generic.HashSet<string>> GetFavoriteTrackIdsAsync();
    Task<bool> IsFavoriteAsync(string trackId);
    Task<bool> ToggleFavoriteAsync(string trackId);

    Task<List<DuplicateGroup>> GetDuplicatesAsync();

    // Music-folder management.
    Task<List<string>> GetMonitoredFoldersAsync();
    Task AddFolderAsync(string path, CancellationToken ct);
    Task RemoveFolderAsync(string path);
    Task RescanAllAsync(CancellationToken ct);

    Task<List<Track>> GetAllTracksAsync();

    Task<List<Artist>> GetAllArtistsAsync();

    Task<List<Album>> GetAllAlbumsAsync();

    Task<List<Track>> GetTracksByAlbumAsync(string albumId);

    Task<List<Track>> GetTracksByArtistAsync(string artistId);

    Task<List<string>> GetGenresAsync();

    Task<List<Track>> GetTracksByGenreAsync(string genre);

    Task<Album?> GetAlbumByIdAsync(string albumId);

    Task<Artist?> GetArtistByIdAsync(string artistId);

    Task<int> GetTotalTrackCountAsync();

    Task<Track?> GetTrackByIdAsync(string trackId);

    Task DeleteTrackAsync(string trackId);

    Task RelocateTrackAsync(string oldTrackId, string newPath);

    Task<SearchResults> SearchLibraryAsync(string query, int? limit = null);
}
