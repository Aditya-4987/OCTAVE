using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Interfaces;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Database;
using Octave.Core.Services.External;
using Octave.Core.Services.External.Artist;
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
    private readonly SqliteDbContext _dbContext;
    private readonly IArtistEnrichmentService? _artistEnrichmentService;
    private readonly IExternalArtworkOrchestrator? _artworkOrchestrator;
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

    [ObservableProperty]
    public partial bool IsEnriching { get; set; }

    public ObservableCollection<Track> Tracks { get; } = new();

    private readonly EventHandler _libraryUpdatedHandler;
    private readonly EventHandler<PlaybackState> _playbackStateChangedHandler;
    private EntityNavigationParameter? _currentParam;

    public EntityDetailViewModel(
        ILibraryService libraryService,
        IQueueService queueService,
        SqliteDbContext dbContext,
        IArtistEnrichmentService? artistEnrichmentService = null,
        IExternalArtworkOrchestrator? artworkOrchestrator = null)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _artistEnrichmentService = artistEnrichmentService;
        _artworkOrchestrator = artworkOrchestrator;
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
    }

    [RelayCommand]
    public async Task EnrichEntityAsync()
    {
        if (_currentParam == null || IsEnriching) return;

        IsEnriching = true;
        try
        {
            if (_currentParam.Type == EntityType.Artist && _artistEnrichmentService != null)
            {
                var profile = await _artistEnrichmentService.GetEnrichedArtistAsync(Title).ConfigureAwait(false);
                if (profile != null)
                {
                    string? newImage = profile.LocalImageToken ?? ArtworkUrl;
                    var updatedArtist = new Artist(_currentParam.Id, Title, profile.Biography, newImage, true);
                    await _dbContext.UpsertArtistAsync(updatedArtist).ConfigureAwait(false);

                    _dispatcher.TryEnqueue(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(profile.Biography)) Description = profile.Biography;
                        if (!string.IsNullOrWhiteSpace(newImage)) ArtworkUrl = newImage;
                    });

                    _libraryService.NotifyLibraryUpdated();
                }
            }
            else if (_currentParam.Type == EntityType.Album && _artworkOrchestrator != null)
            {
                var album = await _libraryService.GetAlbumByIdAsync(_currentParam.Id).ConfigureAwait(false);
                if (album != null)
                {
                    var token = await _artworkOrchestrator.ResolveAndCacheAlbumArtworkAsync(album.Title, album.ArtistName).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        var updatedAlbum = album with { ArtworkUrl = token };
                        await _dbContext.UpsertAlbumAsync(updatedAlbum).ConfigureAwait(false);

                        _dispatcher.TryEnqueue(() =>
                        {
                            ArtworkUrl = token;
                        });

                        _libraryService.NotifyLibraryUpdated();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EntityDetailViewModel] EnrichEntityAsync failed: {ex.Message}");
        }
        finally
        {
            _dispatcher.TryEnqueue(() => IsEnriching = false);
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
