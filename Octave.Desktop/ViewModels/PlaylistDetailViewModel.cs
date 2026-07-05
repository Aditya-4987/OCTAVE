using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;

namespace Octave_Desktop.ViewModels;

public partial class PlaylistDetailViewModel : ObservableObject
{
    private readonly IPlaylistService _playlistService;
    private readonly IQueueService _queueService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int TrackCount { get; set; }

    [ObservableProperty]
    public partial string? CurrentPlayingTrackId { get; set; }

    [ObservableProperty]
    public partial bool IsCurrentlyPlaying { get; set; }

    public ObservableCollection<Track> Tracks { get; } = new();

    private string? _playlistId;
    private bool _isRefreshing;
    private readonly EventHandler<PlaybackState> _playbackStateChangedHandler;

    public PlaylistDetailViewModel(IPlaylistService playlistService, IQueueService queueService)
    {
        _playlistService = playlistService ?? throw new ArgumentNullException(nameof(playlistService));
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

        // Translate a user drag-reorder into a persisted playlist order.
        Tracks.CollectionChanged += OnTracksChanged;
    }

    public void Cleanup()
    {
        _queueService.PlaybackStateChanged -= _playbackStateChangedHandler;
    }

    public void PausePlayback() => _queueService.Pause();
    public void ResumePlayback() => _queueService.Resume();

    public async Task LoadAsync(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId)) return;
        _playlistId = playlistId;

        var playlist = await _playlistService.GetPlaylistByIdAsync(playlistId);
        var tracks = await _playlistService.GetPlaylistTracksAsync(playlistId);

        _dispatcher.TryEnqueue(() =>
        {
            Title = playlist?.Title ?? "Playlist";
            _isRefreshing = true;
            try
            {
                Tracks.Clear();
                foreach (var t in tracks) Tracks.Add(t);
            }
            finally
            {
                _isRefreshing = false;
            }
            TrackCount = Tracks.Count;
            Subtitle = $"{Tracks.Count} Tracks";
        });
    }

    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isRefreshing || _playlistId == null) return;
        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            var orderedIds = Tracks.Select(t => t.Id).ToList();
            _ = _playlistService.SetOrderAsync(_playlistId, orderedIds);
        }
    }

    [RelayCommand]
    private void PlayTrack(Track? targetedTrack)
    {
        if (targetedTrack == null) return;
        _queueService.Clear();
        _queueService.EnqueueRange(Tracks);

        int selectedIndex = -1;
        for (int i = 0; i < Tracks.Count; i++)
        {
            if (Tracks[i].Id == targetedTrack.Id) { selectedIndex = i; break; }
        }
        if (selectedIndex != -1) _queueService.PlayIndex(selectedIndex);
    }

    [RelayCommand]
    private void PlayAll()
    {
        if (Tracks.Count == 0) return;
        _queueService.Clear();
        _queueService.EnqueueRange(Tracks);
        _queueService.PlayIndex(0);
    }

    [RelayCommand]
    private async Task RemoveTrack(Track? track)
    {
        if (track == null || _playlistId == null) return;
        Tracks.Remove(track); // local Remove (ignored by the Move-only reorder handler)
        TrackCount = Tracks.Count;
        Subtitle = $"{Tracks.Count} Tracks";
        await _playlistService.RemoveTrackAsync(_playlistId, track.Id);
    }

    [RelayCommand]
    private async Task DeleteSelf()
    {
        if (_playlistId == null) return;
        await _playlistService.DeletePlaylistAsync(_playlistId);
    }
}
