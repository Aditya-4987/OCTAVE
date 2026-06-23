using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Library;
using System;
using System.Collections.ObjectModel;
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

    public ObservableCollection<Track> Items { get; } = new();

    public LibraryViewModel(ILibraryService libraryService, IQueueService queueService)
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

    public async Task LoadAsync()
    {
        var tracks = await _libraryService.GetAllTracksAsync();
        _dispatcher.TryEnqueue(() =>
        {
            Items.Clear();
            foreach (var track in tracks)
            {
                Items.Add(track);
            }
        });
    }

    [RelayCommand]
    public void PlayTrack(Track targetedTrack)
    {
        if (targetedTrack == null) return;

        _queueService.Clear();
        int selectedIndex = -1;

        for (int i = 0; i < Items.Count; i++)
        {
            var track = Items[i];
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
