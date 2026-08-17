using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Audio;
using Octave.Core.Services.Database;
using Octave.Core.Services.Library;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace Octave_Desktop.ViewModels;

public partial class NowPlayingViewModel : ObservableObject, IDisposable
{
    private readonly IQueueService _queueService;
    private readonly IAudioPlayerService _audioPlayer;
    private readonly ILibraryService _libraryService;
    private readonly ILyricsService _lyricsService;
    private readonly SqliteDbContext _dbContext;
    private readonly DispatcherQueue _dispatcher;

    private CancellationTokenSource? _lyricsCts;
    private CancellationTokenSource? _creditsCts;
    private long _generationToken = 0;
    private bool _isDisposed = false;

    // Track metadata
    [ObservableProperty]
    public partial Track? CurrentTrack { get; set; }

    [ObservableProperty]
    public partial Artist? CurrentArtist { get; set; }

    [ObservableProperty]
    public partial Album? CurrentAlbum { get; set; }

    [ObservableProperty]
    public partial string TrackTitle { get; set; } = "No Track Playing";

    [ObservableProperty]
    public partial string ArtistName { get; set; } = "Unknown Artist";

    [ObservableProperty]
    public partial string AlbumTitle { get; set; } = "";

    [ObservableProperty]
    public partial string? CurrentArtworkUrl { get; set; }

    [ObservableProperty]
    public partial double PositionSeconds { get; set; }

    [ObservableProperty]
    public partial double DurationSeconds { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial AudioQualityDetails? QualityDetails { get; set; }

    // Lyrics State
    [ObservableProperty]
    public partial LyricsState LyricsState { get; set; } = LyricsState.Loading;

    [ObservableProperty]
    public partial IReadOnlyList<LyricLine>? SyncedLines { get; set; }

    [ObservableProperty]
    public partial string? UnsyncedText { get; set; }

    [ObservableProperty]
    public partial int CurrentLyricIndex { get; set; } = -1;

    [ObservableProperty]
    public partial bool IsLyricsPanelVisible { get; set; } = true;

    // Artists List & Queue State
    public ObservableCollection<ArtistDisplayItem> ArtistsList { get; } = new();
    public ObservableCollection<QueueItem> UpNextQueue { get; } = new();

    [ObservableProperty]
    public partial bool IsQueuePanelVisible { get; set; } = true;

    public NowPlayingViewModel(
        IQueueService queueService,
        IAudioPlayerService audioPlayer,
        ILibraryService libraryService,
        ILyricsService lyricsService,
        SqliteDbContext dbContext)
    {
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _lyricsService = lyricsService ?? throw new ArgumentNullException(nameof(lyricsService));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

        _dispatcher = DispatcherQueue.GetForCurrentThread();

        SubscribeEvents();
        RefreshState();
    }

    public void SubscribeEvents()
    {
        _queueService.PlaybackStateChanged += OnPlaybackStateChanged;
        _queueService.PositionChanged += OnPositionChanged;
        _queueService.QueueChanged += OnQueueChanged;
    }

    public void UnsubscribeEvents()
    {
        _queueService.PlaybackStateChanged -= OnPlaybackStateChanged;
        _queueService.PositionChanged -= OnPositionChanged;
        _queueService.QueueChanged -= OnQueueChanged;
    }

    private void OnPlaybackStateChanged(object? sender, PlaybackState state)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            UpdateFromState(state);
        });
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            RefreshUpNextQueue();
        });
    }

    private void OnPositionChanged(object? sender, double positionSeconds)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            PositionSeconds = positionSeconds;
            UpdateLyricPosition(positionSeconds);
        });
    }

    public void RefreshState()
    {
        var state = _queueService.CurrentState;
        UpdateFromState(state);
    }

    private void UpdateFromState(PlaybackState state)
    {
        bool trackChanged = CurrentTrack?.Id != state.CurrentTrack?.Id;

        CurrentTrack = state.CurrentTrack;
        IsPlaying = state.Status == PlaybackStatus.Playing;
        PositionSeconds = state.PositionSeconds;
        DurationSeconds = state.DurationSeconds > 0 ? state.DurationSeconds : (state.CurrentTrack?.DurationSeconds ?? 0);
        QualityDetails = _audioPlayer.QualityDetails;

        if (state.CurrentTrack != null)
        {
            TrackTitle = state.CurrentTrack.Title;
            ArtistName = state.CurrentTrack.ArtistName;
            AlbumTitle = state.CurrentTrack.AlbumTitle;
            CurrentArtworkUrl = CurrentAlbum?.ArtworkUrl;
        }
        else
        {
            TrackTitle = "No Track Playing";
            ArtistName = "Octave Core";
            AlbumTitle = "";
            CurrentArtworkUrl = null;
        }

        RefreshUpNextQueue();

        if (trackChanged)
        {
            long genToken = ++_generationToken;
            _lyricsCts?.Cancel();
            _lyricsCts = new CancellationTokenSource();

            _creditsCts?.Cancel();
            _creditsCts = new CancellationTokenSource();

            if (state.CurrentTrack != null)
            {
                _ = LoadLyricsAsync(state.CurrentTrack, genToken, _lyricsCts.Token);
                _ = LoadCreditsAsync(state.CurrentTrack, genToken, _creditsCts.Token);
            }
            else
            {
                LyricsState = LyricsState.Unavailable;
                SyncedLines = null;
                UnsyncedText = null;
                CurrentLyricIndex = -1;
                IsLyricsPanelVisible = false;
                CurrentArtist = null;
                CurrentAlbum = null;
            }
        }
    }

    private async Task LoadLyricsAsync(Track track, long genToken, CancellationToken ct)
    {
        LyricsState = LyricsState.Loading;
        IsLyricsPanelVisible = true;
        SyncedLines = null;
        UnsyncedText = null;
        CurrentLyricIndex = -1;

        var data = await _lyricsService.GetLyricsAsync(track, ct);

        if (ct.IsCancellationRequested || genToken != _generationToken || track.Id != CurrentTrack?.Id)
        {
            return;
        }

        LyricsState = data.State;

        if (data.State == LyricsState.Synced)
        {
            SyncedLines = data.SyncedLines;
            IsLyricsPanelVisible = true;
            UpdateLyricPosition(PositionSeconds);
        }
        else if (data.State == LyricsState.Unsynced)
        {
            UnsyncedText = data.PlainText;
            IsLyricsPanelVisible = true;
        }
        else
        {
            // Lyrics unavailable: keep visible for 2 seconds, then slide out
            IsLyricsPanelVisible = true;
            try
            {
                await Task.Delay(2000, ct);
                if (!ct.IsCancellationRequested && genToken == _generationToken && track.Id == CurrentTrack?.Id)
                {
                    IsLyricsPanelVisible = false;
                }
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task LoadCreditsAsync(Track track, long genToken, CancellationToken ct)
    {
        Artist? artist = null;
        Album? album = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(track.ArtistId))
            {
                artist = await _dbContext.GetArtistByIdAsync(track.ArtistId);
            }
            if (!string.IsNullOrWhiteSpace(track.AlbumId))
            {
                album = await _dbContext.GetAlbumByIdAsync(track.AlbumId);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NowPlayingViewModel] LoadCreditsAsync error: {ex.Message}");
        }

        if (ct.IsCancellationRequested || genToken != _generationToken || track.Id != CurrentTrack?.Id)
        {
            return;
        }

        CurrentArtist = artist;
        CurrentAlbum = album;
        CurrentArtworkUrl = album?.ArtworkUrl;
        OnPropertyChanged(nameof(ArtistArtworkUrl));
        OnPropertyChanged(nameof(AlbumArtworkUrl));
        OnPropertyChanged(nameof(AlbumYear));

        ArtistsList.Clear();
        string rawArtistNames = !string.IsNullOrWhiteSpace(track.ArtistName) ? track.ArtistName : (artist?.Name ?? "Unknown Artist");

        // If the primary artist matches the raw string exactly (e.g. "AC/DC", "Simon & Garfunkel"), preserve as single artist
        bool isFullMatch = artist != null && artist.Name.Equals(rawArtistNames.Trim(), StringComparison.OrdinalIgnoreCase);

        if (!isFullMatch)
        {
            // Split only on comma, semicolon, or spaced feature/collaboration tokens
            string[] nameParts = System.Text.RegularExpressions.Regex.Split(
                rawArtistNames,
                @"\s*[,;]\s*|\s+(?:feat\.?|ft\.?)\s+|\s+&\s+|\s+/\s+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var validParts = System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(
                    System.Linq.Enumerable.Where(nameParts, n => !string.IsNullOrWhiteSpace(n)),
                    n => n.Trim()));

            if (validParts.Length > 1)
            {
                foreach (string name in validParts)
                {
                    string? artUrl = (artist != null && name.Equals(artist.Name, StringComparison.OrdinalIgnoreCase)) ? artist.ArtworkUrl : null;
                    string? artistId = (artist != null && name.Equals(artist.Name, StringComparison.OrdinalIgnoreCase)) ? artist.Id : null;
                    ArtistsList.Add(new ArtistDisplayItem(artistId, name, artUrl));
                }
            }
            else
            {
                ArtistsList.Add(new ArtistDisplayItem(artist?.Id ?? track.ArtistId, rawArtistNames, artist?.ArtworkUrl));
            }
        }
        else
        {
            ArtistsList.Add(new ArtistDisplayItem(artist?.Id ?? track.ArtistId, artist?.Name ?? rawArtistNames, artist?.ArtworkUrl));
        }
    }

    public string? ArtistArtworkUrl => CurrentArtist?.ArtworkUrl;
    public string? AlbumArtworkUrl => CurrentAlbum?.ArtworkUrl;
    public int AlbumYear => CurrentAlbum?.Year ?? 0;
    public string? CurrentAlbumId => CurrentAlbum?.Id ?? CurrentTrack?.AlbumId;

    public async Task<string?> ResolveArtistIdAsync(ArtistDisplayItem artistItem)
    {
        if (!string.IsNullOrWhiteSpace(artistItem.Id)) return artistItem.Id;

        try
        {
            var artist = await _libraryService.GetArtistByNameAsync(artistItem.Name);
            return artist?.Id;
        }
        catch { return null; }
    }

    public async Task<string?> ResolveAlbumIdAsync()
    {
        if (!string.IsNullOrWhiteSpace(CurrentAlbum?.Id)) return CurrentAlbum.Id;
        if (!string.IsNullOrWhiteSpace(CurrentTrack?.AlbumId)) return CurrentTrack.AlbumId;

        if (!string.IsNullOrWhiteSpace(CurrentTrack?.AlbumTitle))
        {
            try
            {
                var album = await _libraryService.GetAlbumByTitleAsync(CurrentTrack.AlbumTitle, CurrentTrack.ArtistId);
                return album?.Id;
            }
            catch { return null; }
        }
        return null;
    }

    private void UpdateLyricPosition(double currentSeconds)
    {
        if (LyricsState != LyricsState.Synced || SyncedLines == null || SyncedLines.Count == 0)
        {
            CurrentLyricIndex = -1;
            return;
        }

        TimeSpan currentPos = TimeSpan.FromSeconds(currentSeconds);

        // O(1) Boundary check for normal linear playback
        int idx = CurrentLyricIndex;
        if (idx >= 0 && idx < SyncedLines.Count)
        {
            var curLine = SyncedLines[idx];
            TimeSpan start = curLine.Start;
            TimeSpan end = curLine.End ?? (idx < SyncedLines.Count - 1 ? SyncedLines[idx + 1].Start : TimeSpan.MaxValue);

            if (currentPos >= start && currentPos < end)
            {
                return; // Position is within current line bounds: O(1) return!
            }

            if (idx + 1 < SyncedLines.Count)
            {
                var nextLine = SyncedLines[idx + 1];
                TimeSpan nextEnd = nextLine.End ?? (idx + 2 < SyncedLines.Count ? SyncedLines[idx + 2].Start : TimeSpan.MaxValue);
                if (currentPos >= nextLine.Start && currentPos < nextEnd)
                {
                    CurrentLyricIndex = idx + 1; // Advanced 1 line: O(1)!
                    return;
                }
            }
        }

        // O(log N) Binary Search when user seeked or position jumped
        int low = 0;
        int high = SyncedLines.Count - 1;
        int found = -1;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (SyncedLines[mid].Start <= currentPos)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        CurrentLyricIndex = found;
    }

    private void RefreshUpNextQueue()
    {
        var fullQueue = _queueService.GetCurrentQueue();
        UpNextQueue.Clear();

        if (fullQueue == null || fullQueue.Count == 0)
        {
            IsQueuePanelVisible = false;
            return;
        }

        int playingIdx = -1;
        for (int i = 0; i < fullQueue.Count; i++)
        {
            if (fullQueue[i].IsPlaying || fullQueue[i].Track.Id == CurrentTrack?.Id)
            {
                playingIdx = i;
                break;
            }
        }

        int startIdx = (playingIdx >= 0) ? playingIdx : 0;
        for (int i = startIdx; i < fullQueue.Count; i++)
        {
            UpNextQueue.Add(fullQueue[i]);
        }

        IsQueuePanelVisible = UpNextQueue.Count > 0;
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (IsPlaying)
        {
            _queueService.Pause();
        }
        else
        {
            _queueService.Resume();
        }
    }

    [RelayCommand]
    public void PlayQueueItem(QueueItem item)
    {
        if (item == null) return;
        var fullQueue = _queueService.GetCurrentQueue();
        for (int i = 0; i < fullQueue.Count; i++)
        {
            if (fullQueue[i].Id == item.Id)
            {
                _queueService.PlayIndex(i);
                break;
            }
        }
    }

    [RelayCommand]
    public void ClearQueue()
    {
        _queueService.Clear(keepCurrentTrack: true);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _lyricsCts?.Cancel();
        _lyricsCts?.Dispose();
        _creditsCts?.Cancel();
        _creditsCts?.Dispose();
        UnsubscribeEvents();
    }
}
