using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Library;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Octave_Desktop.ViewModels;

public partial class EntityDetailViewModel : ObservableObject
{
    private readonly ILibraryService _libraryService;
    private readonly IQueueService _queueService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Description { get; set; }

    [ObservableProperty]
    public partial string? ArtworkUrl { get; set; }

    [ObservableProperty]
    public partial int TrackCount { get; set; }

    [ObservableProperty]
    public partial string? CurrentPlayingTrackId { get; set; }

    [ObservableProperty]
    public partial bool IsCurrentlyPlaying { get; set; }

    public ObservableCollection<Track> Tracks { get; } = new();

    public EntityDetailViewModel(ILibraryService libraryService, IQueueService queueService)
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

    public async Task LoadEntityAsync(EntityNavigationParameter param)
    {
        if (param == null) return;

        if (param.Type == EntityType.Album)
        {
            var album = await _libraryService.GetAlbumByIdAsync(param.Id);
            if (album == null) return;

            var title = album.Title;
            var subtitle = $"{album.ArtistName} • {album.Year}";
            var artworkUrl = album.ArtworkUrl;
            var tracks = await _libraryService.GetTracksByAlbumAsync(param.Id);

            _dispatcher.TryEnqueue(() =>
            {
                Title = title;
                Subtitle = subtitle;
                ArtworkUrl = artworkUrl;
                Description = null;

                Tracks.Clear();
                foreach (var track in tracks)
                {
                    Tracks.Add(track);
                }
                TrackCount = Tracks.Count;
            });
        }
        else if (param.Type == EntityType.Artist)
        {
            var artist = await _libraryService.GetArtistByIdAsync(param.Id);
            if (artist == null) return;

            var title = artist.Name;
            var artworkUrl = artist.ArtworkUrl;
            var description = artist.Bio;
            var tracks = await _libraryService.GetTracksByArtistAsync(param.Id);

            _dispatcher.TryEnqueue(() =>
            {
                Title = title;
                ArtworkUrl = artworkUrl;
                Description = description;

                Tracks.Clear();
                foreach (var track in tracks)
                {
                    Tracks.Add(track);
                }
                Subtitle = $"{Tracks.Count} Tracks";
                TrackCount = Tracks.Count;
            });
        }
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

    [RelayCommand]
    public void PlayAll()
    {
        if (Tracks.Count == 0) return;

        _queueService.Clear();
        foreach (var track in Tracks)
        {
            _queueService.Enqueue(track);
        }
        _queueService.PlayIndex(0);
    }
}
