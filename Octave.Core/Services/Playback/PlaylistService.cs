using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Database;

namespace Octave.Core.Services.Playback;

public class PlaylistService : IPlaylistService
{
    private readonly SqliteDbContext _dbContext;

    public event EventHandler? PlaylistsChanged;

    public PlaylistService(SqliteDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public Task<List<Playlist>> GetPlaylistsAsync() => _dbContext.GetPlaylistsAsync();

    public Task<Playlist?> GetPlaylistByIdAsync(string id) => _dbContext.GetPlaylistByIdAsync(id);

    public Task<List<Track>> GetPlaylistTracksAsync(string id) => _dbContext.GetPlaylistTracksAsync(id);

    public async Task<Playlist> CreatePlaylistAsync(string title, string? description = null)
    {
        var playlist = await _dbContext.CreatePlaylistAsync(title, description);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return playlist;
    }

    public async Task DeletePlaylistAsync(string id)
    {
        await _dbContext.DeletePlaylistAsync(id);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RenamePlaylistAsync(string id, string title)
    {
        await _dbContext.RenamePlaylistAsync(id, title);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AddTrackAsync(string playlistId, string trackId)
    {
        await _dbContext.AddTrackToPlaylistAsync(playlistId, trackId);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveTrackAsync(string playlistId, string trackId)
    {
        await _dbContext.RemoveTrackFromPlaylistAsync(playlistId, trackId);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetOrderAsync(string playlistId, IReadOnlyList<string> orderedTrackIds)
    {
        await _dbContext.SetPlaylistOrderAsync(playlistId, orderedTrackIds);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }
}
