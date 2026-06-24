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

    public SearchViewModel(ILibraryService libraryService, IQueueService queueService)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        var initialState = _queueService.CurrentState;
        CurrentPlayingTrackId = initialState?.CurrentTrack?.Id;
        IsCurrentlyPlaying = initialState?.Status == PlaybackStatus.Playing;

        _queueService.PlaybackStateChanged += (s, state) =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                CurrentPlayingTrackId = state.CurrentTrack?.Id;
                IsCurrentlyPlaying = state.Status == PlaybackStatus.Playing;
            });
        };
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
        });
    }

    [RelayCommand]
    public void PlayTrack(Track targetedTrack)
    {
        if (targetedTrack == null) return;

        _queueService.Clear();
        int selectedIndex = -1;

        for (int i = 0; i < Tracks.Count; i++)
        {
            var track = Tracks[i];
            _queueService.Enqueue(track);
            if (track.Id == targetedTrack.Id)
            {
                selectedIndex = i;
            }
        }

        if (selectedIndex != -1)
        {
            _queueService.PlayIndex(selectedIndex);
        }
    }
}
