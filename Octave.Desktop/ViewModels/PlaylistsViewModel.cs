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

    public async Task CreateAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        await _playlistService.CreatePlaylistAsync(title.Trim());
    }

    [RelayCommand]
    private async Task DeletePlaylist(Playlist? playlist)
    {
        if (playlist == null) return;
        await _playlistService.DeletePlaylistAsync(playlist.Id);
    }
}
