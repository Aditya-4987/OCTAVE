using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;
using Octave.Core.Services.Database;

using Octave.Core.Interfaces;

namespace Octave.Core.Services.Library;

public class LibraryService : ILibraryService
{
    private readonly SqliteDbContext _dbContext;
    private readonly ILibraryScanner _scanner;
    private readonly ILibraryWatcherService? _watcherService;

    public event EventHandler? LibraryUpdated;
    public event EventHandler? FavoritesChanged;

    public LibraryService(SqliteDbContext dbContext, ILibraryScanner scanner, ILibraryWatcherService? watcherService = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _watcherService = watcherService;

        _scanner.LibraryChanged += (s, e) => LibraryUpdated?.Invoke(this, EventArgs.Empty);
    }

    public Task<List<Track>> GetRecentlyPlayedAsync(int limit) => _dbContext.GetRecentlyPlayedAsync(limit);
    public Task<List<Track>> GetMostPlayedAsync(int limit) => _dbContext.GetMostPlayedAsync(limit);
    public Task<List<Track>> GetLastAddedAsync(int limit) => _dbContext.GetLastAddedAsync(limit);
    public Task<List<Track>> GetFavoritesAsync() => _dbContext.GetFavoritesAsync();
    public Task<System.Collections.Generic.HashSet<string>> GetFavoriteTrackIdsAsync() => _dbContext.GetFavoriteTrackIdsAsync();
    public Task<bool> IsFavoriteAsync(string trackId) => _dbContext.IsFavoriteAsync(trackId);

    public Task<List<DuplicateGroup>> GetDuplicatesAsync() => _dbContext.GetDuplicatesAsync();

    public async Task<bool> ToggleFavoriteAsync(string trackId)
    {
        bool isFav = await _dbContext.IsFavoriteAsync(trackId);
        if (isFav) await _dbContext.RemoveFavoriteAsync(trackId);
        else await _dbContext.AddFavoriteAsync(trackId);
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
        return !isFav;
    }

    public Task ScanLocalLibraryAsync(string rootDir, CancellationToken ct) =>
        _scanner.ScanAsync(rootDir, ct);

    public Task<List<string>> GetMonitoredFoldersAsync() =>
        _dbContext.GetMonitoredFoldersAsync();

    public async Task AddFolderAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        await _dbContext.AddMonitoredFolderAsync(path);
        _scanner.AddMonitoredPath(path);
        _watcherService?.AddMonitoredPath(path);
        await _scanner.ScanAsync(path, ct); // raises LibraryChanged -> LibraryUpdated
    }

    public async Task RemoveFolderAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        await _dbContext.RemoveMonitoredFolderAsync(path);
        _scanner.RemoveMonitoredPath(path);
        _watcherService?.RemoveMonitoredPath(path);
        await _dbContext.DeleteTracksUnderPathAsync(path);
        LibraryUpdated?.Invoke(this, EventArgs.Empty);
    }

    public async Task RescanAllAsync(CancellationToken ct)
    {
        var folders = await _dbContext.GetMonitoredFoldersAsync();
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            await _scanner.ScanAsync(folder, ct);
        }
    }

    public Task<List<Track>> GetAllTracksAsync() => 
        _dbContext.GetAllTracksAsync();

    public Task<List<Artist>> GetAllArtistsAsync() => 
        _dbContext.GetAllArtistsAsync();

    public Task<List<Album>> GetAllAlbumsAsync() => 
        _dbContext.GetAllAlbumsAsync();

    public Task<List<Track>> GetTracksByAlbumAsync(string albumId) => 
        _dbContext.GetTracksByAlbumAsync(albumId);

    public Task<List<Track>> GetTracksByArtistAsync(string artistId) =>
        _dbContext.GetTracksByArtistAsync(artistId);

    public Task<List<string>> GetGenresAsync() =>
        _dbContext.GetGenresAsync();

    public Task<List<Track>> GetTracksByGenreAsync(string genre) =>
        _dbContext.GetTracksByGenreAsync(genre);

    public Task<Album?> GetAlbumByIdAsync(string albumId) =>
        _dbContext.GetAlbumByIdAsync(albumId);

    public Task<Album?> GetAlbumByTitleAsync(string title, string? artistId = null) =>
        _dbContext.GetAlbumByTitleAsync(title, artistId);

    public Task<List<Album>> GetAlbumsByIdsAsync(IEnumerable<string> albumIds) =>
        _dbContext.GetAlbumsByIdsAsync(albumIds);

    public Task<Artist?> GetArtistByIdAsync(string artistId) =>
        _dbContext.GetArtistByIdAsync(artistId);

    public Task<Artist?> GetArtistByNameAsync(string name) =>
        _dbContext.GetArtistByNameAsync(name);

    public Task<int> GetTotalTrackCountAsync() => 
        _dbContext.GetTotalTrackCountAsync();

    public Task<Track?> GetTrackByIdAsync(string trackId) =>
        _dbContext.GetTrackByIdAsync(trackId);

    public async Task DeleteTrackAsync(string trackId)
    {
        await _dbContext.DeleteTrackAsync(trackId);
        LibraryUpdated?.Invoke(this, EventArgs.Empty);
    }

    public async Task RelocateTrackAsync(string oldTrackId, string newPath)
    {
        await _dbContext.RelocateTrackAsync(oldTrackId, newPath);
        LibraryUpdated?.Invoke(this, EventArgs.Empty);
    }

    public Task<SearchResults> SearchLibraryAsync(string query, int? limit = null) =>
        _dbContext.SearchLibraryAsync(query, limit);
}
