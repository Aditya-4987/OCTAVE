using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;
using Octave.Core.Services.Database;

namespace Octave.Core.Services.Library;

public class LibraryService : ILibraryService
{
    private readonly SqliteDbContext _dbContext;
    private readonly LocalLibraryScanner _scanner;

    public LibraryService(SqliteDbContext dbContext, LocalLibraryScanner scanner)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    }

    public Task ScanLocalLibraryAsync(string rootDir, CancellationToken ct) => 
        _scanner.ScanAsync(rootDir, ct);

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

    public Task<Album?> GetAlbumByIdAsync(string albumId) =>
        _dbContext.GetAlbumByIdAsync(albumId);

    public Task<Artist?> GetArtistByIdAsync(string artistId) =>
        _dbContext.GetArtistByIdAsync(artistId);

    public Task<int> GetTotalTrackCountAsync() => 
        _dbContext.GetTotalTrackCountAsync();

    public Task<Track?> GetTrackByIdAsync(string trackId) =>
        _dbContext.GetTrackByIdAsync(trackId);

    public Task<SearchResults> SearchLibraryAsync(string query, int? limit = null) =>
        _dbContext.SearchLibraryAsync(query, limit);
}
