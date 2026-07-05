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

    private readonly EventHandler _libraryUpdatedHandler;
    private readonly EventHandler<PlaybackState> _playbackStateChangedHandler;
    private EntityNavigationParameter? _currentParam;

    public EntityDetailViewModel(ILibraryService libraryService, IQueueService queueService)
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
            if (_currentParam != null)
            {
                _ = LoadEntityAsync(_currentParam);
            }
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

    public async Task LoadEntityAsync(EntityNavigationParameter param)
    {
        if (param == null) return;
        _currentParam = param;

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
        else if (param.Type == EntityType.Genre)
        {
            // The genre name is carried directly in the navigation Id.
            var tracks = await _libraryService.GetTracksByGenreAsync(param.Id);

            _dispatcher.TryEnqueue(() =>
            {
                Title = param.Id;
                ArtworkUrl = null;
                Description = null;

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

    [RelayCommand]
    public void PlayAll()
    {
        if (Tracks.Count == 0) return;

        _queueService.Clear();
        _queueService.EnqueueRange(Tracks);
        _queueService.PlayIndex(0);
    }
}
