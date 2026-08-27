using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Library;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Octave_Desktop.ViewModels;

public partial class PlaylistDetailViewModel : ObservableObject
{
    private readonly IPlaylistService _playlistService;
    private readonly IQueueService _queueService;
    private readonly ILibraryService _libraryService;
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

    [ObservableProperty]
    public partial bool IsSelectionMode { get; set; }

    public ObservableCollection<Track> Tracks { get; } = new();

    private string? _playlistId;
    public string? PlaylistId => _playlistId;
    private bool _isRefreshing;
    private readonly EventHandler<PlaybackState> _playbackStateChangedHandler;

    // VM-11: one debounced persist after a reorder settles instead of one
    // fire-and-forget write per CollectionChanged event.
    private const int ReorderPersistDelayMs = 400;
    private CancellationTokenSource? _reorderPersistCts;

    public PlaylistDetailViewModel(IPlaylistService playlistService, IQueueService queueService, ILibraryService libraryService)
    {
        _playlistService = playlistService ?? throw new ArgumentNullException(nameof(playlistService));
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
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

        // VM-11: cancel any pending debounced persist. Trade-off: a reorder in
        // the last ~400ms before leaving the page may not reach the database -
        // preferred over leaking the CTS or blocking the UI thread to flush.
        _reorderPersistCts?.Cancel();
        _reorderPersistCts?.Dispose();
        _reorderPersistCts = null;
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
                // VM-10: skip the rebuild when the sequence didn't change.
                if (!Helpers.CollectionDiff.SameIdSequence(Tracks, tracks, t => t.Id))
                {
                    Tracks.Clear();
                    foreach (var t in tracks) Tracks.Add(t);
                }
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

        // VM-11: a ListView drag-reorder can surface as Move OR as a Remove+Add
        // pair depending on how the drop lands - persist for ANY structural
        // mutation caused by user interaction, not just Move (Remove+Add-style
        // reorders used to be silently unpersisted).
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Move:
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Add:
            case NotifyCollectionChangedAction.Replace:
                ScheduleOrderPersist();
                break;
        }
    }

    private void ScheduleOrderPersist()
    {
        // VM-11: cancel the previous pending write and start the settle window
        // over - rapid drag sequences collapse into one SetOrderAsync.
        _reorderPersistCts?.Cancel();
        _reorderPersistCts?.Dispose();
        _reorderPersistCts = new CancellationTokenSource();

        var playlistId = _playlistId!;
        var cts = _reorderPersistCts;
        _ = PersistOrderAfterSettleAsync(playlistId, cts.Token);
    }

    private async Task PersistOrderAfterSettleAsync(string playlistId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ReorderPersistDelayMs, ct);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer edit
        }

        // Resumed on the UI thread (raised from CollectionChanged there): read
        // the settled order before going async again.
        var orderedIds = Tracks.Select(t => t.Id).ToList();

        try
        {
            await _playlistService.SetOrderAsync(playlistId, orderedIds);
        }
        catch (Exception ex)
        {
            // VM-11: the old fire-and-forget write had no error handling at all.
            System.Diagnostics.Debug.WriteLine($"[PlaylistDetailViewModel] Order persist failed: {ex.Message}");
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
        // Local remove; also feeds OnTracksChanged, which schedules a debounced
        // order persist so the remaining sequence stays consistent on disk.
        Tracks.Remove(track);
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

    public Task<List<Track>> GetAllLibraryTracksAsync() => _libraryService.GetAllTracksAsync();

    public async Task AddTracksToPlaylistAsync(IEnumerable<Track> newTracks)
    {
        if (_playlistId == null) return;
        var trackList = newTracks?.ToList();
        if (trackList == null || trackList.Count == 0) return;

        await _playlistService.AddTracksAsync(_playlistId, trackList.Select(t => t.Id));
        await LoadAsync(_playlistId);
    }

    public async Task RemoveSelectedTracksAsync(IEnumerable<Track> selectedTracks)
    {
        if (_playlistId == null) return;
        var trackList = selectedTracks?.ToList();
        if (trackList == null || trackList.Count == 0) return;

        foreach (var t in trackList)
        {
            Tracks.Remove(t);
            await _playlistService.RemoveTrackAsync(_playlistId, t.Id);
        }

        TrackCount = Tracks.Count;
        Subtitle = $"{Tracks.Count} Tracks";
    }

    public void PlaySelectedTracks(IEnumerable<Track> selectedTracks)
    {
        var trackList = selectedTracks?.ToList();
        if (trackList == null || trackList.Count == 0) return;

        _queueService.Clear();
        _queueService.EnqueueRange(trackList);
        _queueService.PlayIndex(0);
    }

    public void AddSelectedTracksToQueue(IEnumerable<Track> selectedTracks)
    {
        var trackList = selectedTracks?.ToList();
        if (trackList == null || trackList.Count == 0) return;

        _queueService.EnqueueRange(trackList);
    }
}
