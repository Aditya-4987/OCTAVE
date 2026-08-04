using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Models;
using Octave.Core.Services.Library;
using Octave.Core.Interfaces;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Octave_Desktop.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    private readonly ILibraryService _libraryService;
    private readonly IQueueService _queueService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    [ObservableProperty]
    public partial string? CurrentPlayingTrackId { get; set; }

    [ObservableProperty]
    public partial bool IsCurrentlyPlaying { get; set; }

    public ObservableCollection<Track> Tracks { get; } = new();
    public ObservableCollection<Album> Albums { get; } = new();
    public ObservableCollection<Artist> Artists { get; } = new();
    public ObservableCollection<Playlist> Playlists { get; } = new();

    private readonly EventHandler<PlaybackState> _playbackStateChangedHandler;

    public SearchViewModel(ILibraryService libraryService, IQueueService queueService)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        var initialState = _queueService.CurrentState;
        CurrentPlayingTrackId = initialState?.CurrentTrack?.Id;
        IsCurrentlyPlaying = initialState?.Status == PlaybackStatus.Playing;

        // Stored in a field (not an inline lambda) so it can be detached in
        // Cleanup() - this transient VM subscribes to a singleton service and
        // would otherwise leak on every navigation to the search page.
        _playbackStateChangedHandler = (s, state) =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                CurrentPlayingTrackId = state.CurrentTrack?.Id;
                IsCurrentlyPlaying = state.Status == PlaybackStatus.Playing;
            });
        };
        _queueService.PlaybackStateChanged += _playbackStateChangedHandler;
    }

    public void Cleanup()
    {
        _queueService.PlaybackStateChanged -= _playbackStateChangedHandler;
    }

    public void PausePlayback() => _queueService.Pause();
    public void ResumePlayback() => _queueService.Resume();

    public async Task ExecuteSearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _dispatcher.TryEnqueue(() =>
            {
                Tracks.Clear();
                Albums.Clear();
                Artists.Clear();
                Playlists.Clear();
            });
            return;
        }

        var results = await _libraryService.SearchLibraryAsync(query);

        _dispatcher.TryEnqueue(() =>
        {
            Tracks.Clear();
            foreach (var track in results.Tracks)
            {
                Tracks.Add(track);
            }

            Albums.Clear();
            foreach (var album in results.Albums)
            {
                Albums.Add(album);
            }

            Artists.Clear();
            foreach (var artist in results.Artists)
            {
                Artists.Add(artist);
            }

            Playlists.Clear();
            if (results.Playlists != null)
            {
                foreach (var playlist in results.Playlists)
                {
                    Playlists.Add(playlist);
                }
            }
        });
    }

    [RelayCommand]
    public void PlayTrack(Track targetedTrack)
    {
        if (targetedTrack == null) return;

        _queueService.Clear();
        _queueService.EnqueueRange(Tracks);

        int selectedIndex = -1;
        for (int i = 0; i < Tracks.Count; i++)
        {
            if (Tracks[i].Id == targetedTrack.Id)
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
}
