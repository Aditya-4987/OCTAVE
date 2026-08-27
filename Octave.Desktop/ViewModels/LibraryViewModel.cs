using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Library;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Octave_Desktop.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private readonly ILibraryService _libraryService;
    private readonly IQueueService _queueService;
    private readonly IPlaylistService _playlistService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    [ObservableProperty]
    public partial string? CurrentPlayingTrackId { get; set; }

    [ObservableProperty]
    public partial bool IsCurrentlyPlaying { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsSelectionMode { get; set; }

    // 0=Title, 1=Artist, 2=Album, 3=Date Added, 4=Duration
    [ObservableProperty]
    public partial int SortIndex { get; set; }

    public ObservableCollection<LibraryTrackItem> Items { get; } = new();
    private List<Track> _allTracks = new();
    // NF-37: albumId -> album artwork token, resolved once per library load and
    // reapplied when the list is re-sorted (sorting rebuilds the row wrappers).
    private readonly Dictionary<string, string?> _albumArtTokens = new(StringComparer.Ordinal);
    private readonly HashSet<string> _favoriteTrackIds = new(StringComparer.Ordinal);

    private readonly EventHandler _libraryUpdatedHandler;
    private readonly EventHandler _favoritesChangedHandler;
    private readonly EventHandler<PlaybackState> _playbackStateChangedHandler;

    public LibraryViewModel(ILibraryService libraryService, IQueueService queueService, IPlaylistService playlistService)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _playlistService = playlistService ?? throw new ArgumentNullException(nameof(playlistService));
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        var initialState = _queueService.CurrentState;
        CurrentPlayingTrackId = initialState?.CurrentTrack?.Id;
        IsCurrentlyPlaying = initialState?.Status == PlaybackStatus.Playing;

        _playbackStateChangedHandler = (s, state) =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                CurrentPlayingTrackId = state.CurrentTrack?.Id;
                IsCurrentlyPlaying = state.Status == PlaybackStatus.Playing;
            });
        };
        _queueService.PlaybackStateChanged += _playbackStateChangedHandler;

        _libraryUpdatedHandler = (s, e) =>
        {
            _ = LoadAsync();
        };
        _libraryService.LibraryUpdated += _libraryUpdatedHandler;

        _favoritesChangedHandler = async (s, e) =>
        {
            try
            {
                var favIds = await _libraryService.GetFavoriteTrackIdsAsync();
                _dispatcher.TryEnqueue(() =>
                {
                    _favoriteTrackIds.Clear();
                    foreach (var id in favIds) _favoriteTrackIds.Add(id);
                    foreach (var item in Items)
                    {
                        item.IsFavorite = _favoriteTrackIds.Contains(item.Track.Id);
                    }
                });
            }
            catch { }
        };
        _libraryService.FavoritesChanged += _favoritesChangedHandler;
    }

    public void Cleanup()
    {
        _queueService.PlaybackStateChanged -= _playbackStateChangedHandler;
        _libraryService.LibraryUpdated -= _libraryUpdatedHandler;
        _libraryService.FavoritesChanged -= _favoritesChangedHandler;
    }

    public void PausePlayback() => _queueService.Pause();
    public void ResumePlayback() => _queueService.Resume();

    public async Task<bool> ToggleFavoriteAsync(string trackId)
    {
        bool newFav = await _libraryService.ToggleFavoriteAsync(trackId);
        if (newFav) _favoriteTrackIds.Add(trackId);
        else _favoriteTrackIds.Remove(trackId);
        return newFav;
    }

    public async Task LoadAsync()
    {
        _dispatcher.TryEnqueue(() => IsLoading = Items.Count == 0);
        try
        {
            var tracks = await _libraryService.GetAllTracksAsync();
            // NF-37: album art tokens for the per-row thumbnails, fetched in one
            // batch alongside the tracks (Track carries no artwork of its own).
            var albums = await _libraryService.GetAllAlbumsAsync();
            var favIds = await _libraryService.GetFavoriteTrackIdsAsync();
            _dispatcher.TryEnqueue(() =>
            {
                _allTracks = tracks;
                _albumArtTokens.Clear();
                foreach (var album in albums)
                {
                    _albumArtTokens[album.Id] = album.ArtworkUrl ?? "";
                }
                _favoriteTrackIds.Clear();
                foreach (var id in favIds)
                {
                    _favoriteTrackIds.Add(id);
                }
                ApplySort();
                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            // VM-06: reset the spinner even when the DB read fails - IsLoading
            // used to stay true forever on a failed load.
            _dispatcher.TryEnqueue(() => IsLoading = false);
            System.Diagnostics.Debug.WriteLine($"[LibraryViewModel] LoadAsync failed: {ex.Message}");
        }
    }

    partial void OnSortIndexChanged(int value) => ApplySort();

    private void ApplySort()
    {
        var sorted = (SortIndex switch
        {
            1 => _allTracks.OrderBy(t => t.ArtistName, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(t => t.AlbumTitle, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(t => t.TrackNumber),
            2 => _allTracks.OrderBy(t => t.AlbumTitle, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(t => t.TrackNumber),
            3 => _allTracks.OrderByDescending(t => t.DateAdded),
            4 => _allTracks.OrderBy(t => t.DurationSeconds),
            _ => _allTracks.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
        }).ToList();

        if (Items.Count == sorted.Count && Items.Select(i => i.Track).SequenceEqual(sorted))
        {
            return;
        }

        Items.Clear();
        foreach (var track in sorted)
        {
            var item = new LibraryTrackItem(track);
            // NF-37: apply the already-resolved album token so the thumbnail is
            // present on first render; unknown albums fall back to the placeholder.
            if (_albumArtTokens.TryGetValue(track.AlbumId, out var token))
            {
                item.ArtworkUrl = token;
            }
            item.IsFavorite = _favoriteTrackIds.Contains(track.Id);
            Items.Add(item);
        }
    }

    [RelayCommand]
    public void PlayTrack(Track targetedTrack)
    {
        if (targetedTrack == null) return;

        _queueService.Clear();
        _queueService.EnqueueRange(Items.Select(i => i.Track));

        int selectedIndex = -1;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Id == targetedTrack.Id)
            {
                selectedIndex = i;
                break;
            }
        }

        if (selectedIndex != -1)
        {
            _queueService.PlayIndex(selectedIndex);
        }
    }

    public void PlaySelectedTracks(IEnumerable<Track> tracks)
    {
        var trackList = tracks?.ToList();
        if (trackList == null || trackList.Count == 0) return;

        _queueService.Clear();
        _queueService.EnqueueRange(trackList);
        _queueService.PlayIndex(0);
    }

    public void AddSelectedTracksToQueue(IEnumerable<Track> tracks)
    {
        var trackList = tracks?.ToList();
        if (trackList == null || trackList.Count == 0) return;

        _queueService.EnqueueRange(trackList);
    }

    public Task<List<Playlist>> GetPlaylistsAsync() => _playlistService.GetPlaylistsAsync();

    public async Task AddSelectedTracksToPlaylistAsync(string playlistId, IEnumerable<Track> tracks)
    {
        var trackIds = tracks?.Select(t => t.Id).ToList();
        if (trackIds == null || trackIds.Count == 0) return;

        await _playlistService.AddTracksAsync(playlistId, trackIds);
    }

    public async Task<Playlist> CreatePlaylistWithTracksAsync(string name, IEnumerable<Track> tracks)
    {
        var playlist = await _playlistService.CreatePlaylistAsync(name);
        var trackIds = tracks?.Select(t => t.Id).ToList();
        if (trackIds != null && trackIds.Count > 0)
        {
            await _playlistService.AddTracksAsync(playlist.Id, trackIds);
        }
        return playlist;
    }
}
