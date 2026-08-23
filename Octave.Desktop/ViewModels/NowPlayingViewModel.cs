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
    private bool _eventsSubscribed = false;

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

    // CRIT-01: the constructor is the single subscription point; the guard keeps a
    // stray second call (e.g. a page Loaded handler) from doubling every event.
    public void SubscribeEvents()
    {
        if (_eventsSubscribed) return;
        _eventsSubscribed = true;

        _queueService.PlaybackStateChanged += OnPlaybackStateChanged;
        _queueService.PositionChanged += OnPositionChanged;
        _queueService.QueueChanged += OnQueueChanged;
    }

    public void UnsubscribeEvents()
    {
        if (!_eventsSubscribed) return;
        _eventsSubscribed = false;

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
            // CRIT-04: CurrentAlbum lags one async hop behind (it loads in
            // LoadCreditsAsync below), so deriving artwork from it here blanked the
            // panel on every track change — the "artwork flash". Keep showing the
            // previous album's art until the new album (and its art) resolves there;
            // only a real stop clears it.
        }
        else
        {
            TrackTitle = "No Track Playing";
            ArtistName = "Octave Core";
            AlbumTitle = "";
            CurrentArtworkUrl = null;
        }

        // VM-03: RefreshUpNextQueue used to run here AND in OnQueueChanged — the
        // queue service emits PlaybackStateChanged + QueueChanged back-to-back, so
        // every transition rebuilt the list twice. QueueChanged alone owns it now.

        if (trackChanged)
        {
            long genToken = ++_generationToken;

            // CRIT-02: cancel AND dispose the old sources - they were cancelled
            // but never disposed, leaking a native wait handle on every track
            // change. Dispose() at the bottom of this class already did both.
            _lyricsCts?.Cancel();
            _lyricsCts?.Dispose();
            _lyricsCts = new CancellationTokenSource();

            _creditsCts?.Cancel();
            _creditsCts?.Dispose();
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

        // VM-08: these property writes used to land on whichever thread the
        // lyrics await resumed on - apply them through the dispatcher like every
        // other mutation in this VM.
        _dispatcher.TryEnqueue(() =>
        {
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
                // (the delayed hide below).
                IsLyricsPanelVisible = true;
            }
        });

        if (data.State != LyricsState.Synced && data.State != LyricsState.Unsynced)
        {
            try
            {
                await Task.Delay(2000, ct);
                if (!ct.IsCancellationRequested && genToken == _generationToken && track.Id == CurrentTrack?.Id)
                {
                    _dispatcher.TryEnqueue(() => IsLyricsPanelVisible = false);
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

        string rawArtistNames = !string.IsNullOrWhiteSpace(track.ArtistName) ? track.ArtistName : (artist?.Name ?? "Unknown Artist");

        // NP-08: compare with collapsed whitespace + case-folding — a DB name like
        // "Simon &  Garfunkel" or casing drift used to fail the strict equality, fall
        // through to the split regex, and shred "Simon & Garfunkel" into two artists.
        static string NormalizeArtistKey(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");

        // If the primary artist matches the raw string (e.g. "AC/DC", "Simon & Garfunkel"), preserve as single artist
        bool isFullMatch = artist != null &&
            NormalizeArtistKey(artist.Name).Equals(NormalizeArtistKey(rawArtistNames), StringComparison.Ordinal);

        // VM-08: the credit writes and the ArtistsList rebuild used to run on the
        // DB await's resume thread - marshal them onto the UI thread.
        _dispatcher.TryEnqueue(() =>
        {
            CurrentArtist = artist;
            CurrentAlbum = album;
            CurrentArtworkUrl = album?.ArtworkUrl;
            OnPropertyChanged(nameof(ArtistArtworkUrl));
            OnPropertyChanged(nameof(AlbumArtworkUrl));
            OnPropertyChanged(nameof(AlbumYear));

            ArtistsList.Clear();

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
        });
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

        if (fullQueue == null || fullQueue.Count == 0)
        {
            UpNextQueue.Clear();
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

        // NP-09: only rebuild the ObservableCollection when the visible window
        // actually changed — Clear()+re-add on every state pulse flickered the whole
        // ListView even when the queue was untouched.
        int newCount = fullQueue.Count - startIdx;
        if (newCount == UpNextQueue.Count)
        {
            bool identical = true;
            for (int i = 0; i < newCount; i++)
            {
                if (UpNextQueue[i].Id != fullQueue[startIdx + i].Id) { identical = false; break; }
            }
            if (identical)
            {
                return;
            }
        }

        UpNextQueue.Clear();
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
        // NP-10: resolve the index inside the queue service under its lock — the old
        // copy-then-scan duplicated work per click and could act on a stale snapshot.
        _queueService.PlayQueueItem(item.Id);
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
