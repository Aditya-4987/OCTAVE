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
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    [ObservableProperty]
    public partial string? CurrentPlayingTrackId { get; set; }

    [ObservableProperty]
    public partial bool IsCurrentlyPlaying { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    // 0=Title, 1=Artist, 2=Album, 3=Date Added, 4=Duration
    [ObservableProperty]
    public partial int SortIndex { get; set; }

    public ObservableCollection<Track> Items { get; } = new();
    private List<Track> _allTracks = new();

    private readonly EventHandler _libraryUpdatedHandler;
    private readonly EventHandler<PlaybackState> _playbackStateChangedHandler;

    public LibraryViewModel(ILibraryService libraryService, IQueueService queueService)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
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
    }

    public void Cleanup()
    {
        _queueService.PlaybackStateChanged -= _playbackStateChangedHandler;
        _libraryService.LibraryUpdated -= _libraryUpdatedHandler;
    }

    public void PausePlayback() => _queueService.Pause();
    public void ResumePlayback() => _queueService.Resume();

    public async Task LoadAsync()
    {
        _dispatcher.TryEnqueue(() => IsLoading = Items.Count == 0);
        var tracks = await _libraryService.GetAllTracksAsync();
        _dispatcher.TryEnqueue(() =>
        {
            _allTracks = tracks;
            ApplySort();
            IsLoading = false;
        });
    }

    partial void OnSortIndexChanged(int value) => ApplySort();

    private void ApplySort()
    {
        IEnumerable<Track> sorted = SortIndex switch
        {
            1 => _allTracks.OrderBy(t => t.ArtistName, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(t => t.AlbumTitle, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(t => t.TrackNumber),
            2 => _allTracks.OrderBy(t => t.AlbumTitle, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(t => t.TrackNumber),
            3 => _allTracks.OrderByDescending(t => t.DateAdded),
            4 => _allTracks.OrderBy(t => t.DurationSeconds),
            _ => _allTracks.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
        };

        Items.Clear();
        foreach (var track in sorted) Items.Add(track);
    }

    [RelayCommand]
    public void PlayTrack(Track targetedTrack)
    {
        if (targetedTrack == null) return;

        _queueService.Clear();
        _queueService.EnqueueRange(Items);

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
}
