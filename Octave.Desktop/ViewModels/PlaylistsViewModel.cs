using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Octave_Desktop.ViewModels;

public partial class PlaylistsViewModel : ObservableObject
{
    private readonly IPlaylistService _playlistService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    public ObservableCollection<Playlist> Items { get; } = new();

    private readonly EventHandler _changedHandler;

    public PlaylistsViewModel(IPlaylistService playlistService)
    {
        _playlistService = playlistService ?? throw new ArgumentNullException(nameof(playlistService));
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _changedHandler = (s, e) => { _ = LoadAsync(); };
        _playlistService.PlaylistsChanged += _changedHandler;
    }

    public void Cleanup()
    {
        _playlistService.PlaylistsChanged -= _changedHandler;
    }

    public async Task LoadAsync()
    {
        try
        {
            var playlists = await _playlistService.GetPlaylistsAsync();
            _dispatcher.TryEnqueue(() =>
            {
                if (Items.Count == playlists.Count && System.Linq.Enumerable.SequenceEqual(Items, playlists))
                {
                    return;
                }

                Items.Clear();
                foreach (var p in playlists) Items.Add(p);
            });
        }
        catch (Exception ex)
        {
            // VM-06: fire-and-forget off LibraryUpdated - don't let a failed
            // DB read vanish silently.
            System.Diagnostics.Debug.WriteLine($"[PlaylistsViewModel] LoadAsync failed: {ex.Message}");
        }
    }

    public async Task CreateAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        try
        {
            await _playlistService.CreatePlaylistAsync(title.Trim());
            await LoadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlaylistsViewModel] CreateAsync failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeletePlaylist(Playlist? playlist)
    {
        if (playlist == null) return;
        try
        {
            await _playlistService.DeletePlaylistAsync(playlist.Id);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            // VM-06 (NF): a failed delete used to leave the stale playlist in
            // the list with no refresh attempt and no trace of the error.
            System.Diagnostics.Debug.WriteLine($"[PlaylistsViewModel] DeletePlaylist failed: {ex.Message}");
        }
    }
}
