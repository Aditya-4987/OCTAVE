using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Interfaces;

public interface IPlaylistService
{
    // Raised whenever the set of playlists or their contents change.
    event EventHandler? PlaylistsChanged;

    Task<List<Playlist>> GetPlaylistsAsync();
    Task<Playlist?> GetPlaylistByIdAsync(string id);
    Task<List<Track>> GetPlaylistTracksAsync(string id);
    Task<List<PlaylistTrackEntry>> GetPlaylistTrackEntriesAsync(string id);

    Task<Playlist> CreatePlaylistAsync(string title, string? description = null);
    Task DeletePlaylistAsync(string id);
    Task RenamePlaylistAsync(string id, string title);

    Task AddTrackAsync(string playlistId, string trackId);
    Task AddTracksAsync(string playlistId, IEnumerable<string> trackIds);
    Task RemoveTrackAsync(string playlistId, string trackId);
    Task RemoveTrackEntryAsync(string entryId);
    Task SetOrderAsync(string playlistId, IReadOnlyList<string> orderedTrackOrEntryIds);
}
