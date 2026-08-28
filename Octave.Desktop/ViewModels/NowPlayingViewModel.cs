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
    private CancellationTokenSource? _prewarmCts;
    private string? _lastPrewarmedTrackId;
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

    private double _positionSeconds;
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            double safe = value;
            if (double.IsNaN(safe) || double.IsInfinity(safe) || safe < 0 || DurationSeconds <= 0)
            {
                safe = 0.0;
            }
            else if (safe > DurationSeconds)
            {
                safe = DurationSeconds;
            }

            if (Math.Abs(_positionSeconds - safe) > 0.0001)
            {
                SetProperty(ref _positionSeconds, safe);
            }
        }
    }

    private double _durationSeconds;
    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            double safe = value;
            if (double.IsNaN(safe) || double.IsInfinity(safe) || safe < 0) safe = 0.0;

            if (Math.Abs(_durationSeconds - safe) > 0.0001)
            {
                SetProperty(ref _durationSeconds, safe);
                OnPropertyChanged(nameof(SliderMaximum));
                OnPropertyChanged(nameof(IsTimelineEnabled));
                if (safe <= 0 || _positionSeconds > safe)
                {
                    PositionSeconds = 0.0;
                }
            }
        }
    }

    public double SliderMaximum => DurationSeconds > 0 ? DurationSeconds : 100.0;
    public bool IsTimelineEnabled => DurationSeconds > 0;

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool IsCurrentTrackFavorite { get; set; } = false;

    [ObservableProperty]
    public partial AudioQualityDetails? QualityDetails { get; set; }

    public string? ArtistArtworkUrl => CurrentArtist?.ArtworkUrl;
    public string? AlbumArtworkUrl => CurrentAlbum?.ArtworkUrl;
    public int AlbumYear => CurrentAlbum?.Year ?? (CurrentTrack?.Year > 0 ? CurrentTrack.Year : 0);
    public string TrackGenre => CurrentTrack?.Genre ?? "";
    public int TrackNumber => CurrentTrack?.TrackNumber ?? 0;
    public int DiscNumber => CurrentTrack?.DiscNumber ?? 0;
    public double? ReplayGain => CurrentTrack != null ? (double)CurrentTrack.ReplayGain : null;
    public string SourceUri => CurrentTrack?.SourceUri ?? "";

    // Lyrics State
    [ObservableProperty]
    public partial LyricsState LyricsState { get; set; } = LyricsState.Loading;

    [ObservableProperty]
    public partial LyricDisplayMode SelectedLyricMode { get; set; } = LyricDisplayMode.Synced;

    [ObservableProperty]
    public partial bool HasSyncedLyrics { get; set; } = false;

    [ObservableProperty]
    public partial bool HasPlainLyrics { get; set; } = false;

    [ObservableProperty]
    public partial IReadOnlyList<LyricLine>? SyncedLines { get; set; }

    [ObservableProperty]
    public partial string? UnsyncedText { get; set; }

    [ObservableProperty]
    public partial string? LyricsSource { get; set; }

    [ObservableProperty]
    public partial int CurrentLyricIndex { get; set; } = -1;

    private LyricDisplayMode? _userSelectedMode;
    private LyricsData? _cachedLyricsData;

    // UI-NP-06: user-applied sync offset for out-of-sync LRC files, in ms.
    // Positive values make lyric lines fire LATER (the effective position is
    // shifted back before comparing against line timestamps). Per-track: reset
    // on every track change, since drift differs per file.
    [ObservableProperty]
    public partial int LyricsOffsetMs { get; set; } = 0;

    [ObservableProperty]
    public partial bool IsLyricsPanelVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsReloadingLyrics { get; set; } = false;

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

        UpNextQueue.CollectionChanged += OnUpNextQueueChanged;

        SubscribeEvents();
        RefreshState();
    }

    public void SubscribeEvents()
    {
        if (_eventsSubscribed) return;
        _eventsSubscribed = true;

        _queueService.PlaybackStateChanged += OnPlaybackStateChanged;
        _queueService.QueueChanged += OnQueueChanged;
        _audioPlayer.PositionChanged += OnPositionChanged;
        _libraryService.FavoritesChanged += OnFavoritesChanged;
    }

    public void UnsubscribeEvents()
    {
        if (!_eventsSubscribed) return;
        _eventsSubscribed = false;

        _queueService.PlaybackStateChanged -= OnPlaybackStateChanged;
        _queueService.QueueChanged -= OnQueueChanged;
        _audioPlayer.PositionChanged -= OnPositionChanged;
        _libraryService.FavoritesChanged -= OnFavoritesChanged;
    }

    private void OnFavoritesChanged(object? sender, EventArgs e)
    {
        var trackId = CurrentTrack?.Id;
        if (trackId != null)
        {
            _ = CheckFavoriteStatusAsync(trackId);
        }
    }

    [RelayCommand]
    public async Task ToggleCurrentTrackFavoriteAsync()
    {
        var trackId = CurrentTrack?.Id;
        if (trackId != null)
        {
            bool newFav = await _libraryService.ToggleFavoriteAsync(trackId);
            IsCurrentTrackFavorite = newFav;
        }
    }

    private async Task CheckFavoriteStatusAsync(string trackId)
    {
        try
        {
            bool isFav = await _libraryService.IsFavoriteAsync(trackId);
            _dispatcher.TryEnqueue(() =>
            {
                if (_isDisposed || CurrentTrack?.Id != trackId) return;
                IsCurrentTrackFavorite = isFav;
            });
        }
        catch { }
    }

    public void RefreshState()
    {
        var state = _queueService.CurrentState;
        UpdateFromState(state);
        RefreshUpNextQueue();
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

    private void OnPositionChanged(object? sender, double position)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            PositionSeconds = position;
            UpdateLyricPosition(position);

            if (DurationSeconds > 0 && (position / DurationSeconds) >= 0.75)
            {
                MaybePrewarmNextTrackLyrics();
            }
        });
    }

    private void UpdateFromState(PlaybackState state)
    {
        bool trackChanged = CurrentTrack?.Id != state.CurrentTrack?.Id;

        CurrentTrack = state.CurrentTrack;
        IsPlaying = state.Status == PlaybackStatus.Playing;
        DurationSeconds = state.DurationSeconds > 0 ? state.DurationSeconds : (state.CurrentTrack?.DurationSeconds ?? 0);
        PositionSeconds = state.PositionSeconds;
        QualityDetails = _audioPlayer.QualityDetails;

        if (state.CurrentTrack != null)
        {
            TrackTitle = state.CurrentTrack.Title;
            ArtistName = state.CurrentTrack.ArtistName;
            AlbumTitle = state.CurrentTrack.AlbumTitle;
        }
        else
        {
            TrackTitle = "No Track Playing";
            ArtistName = "Octave Core";
            AlbumTitle = "";
            CurrentArtworkUrl = null;
        }

        if (trackChanged)
        {
            long reqId = Interlocked.Increment(ref _globalLyricsRequestId);
            _activeLyricsRequestId = reqId;
            _generationToken = reqId;
            _lastLoadedTrackId = state.CurrentTrack?.Id;

            // Reset per-track lyrics selection and offset
            _userSelectedMode = null;
            _cachedLyricsData = null;
            HasSyncedLyrics = false;
            HasPlainLyrics = false;
            LyricsSource = null;
            LyricsOffsetMs = 0;
            IsReloadingLyrics = false;

            // CRIT-02: cancel AND dispose the old sources safely
            var oldLyricsCts = Interlocked.Exchange(ref _lyricsCts, new CancellationTokenSource());
            try
            {
                oldLyricsCts?.Cancel();
                oldLyricsCts?.Dispose();
            }
            catch { }

            var oldCreditsCts = Interlocked.Exchange(ref _creditsCts, new CancellationTokenSource());
            try
            {
                oldCreditsCts?.Cancel();
                oldCreditsCts?.Dispose();
            }
            catch { }

            if (state.CurrentTrack != null)
            {
                _ = CheckFavoriteStatusAsync(state.CurrentTrack.Id);
                _ = LoadLyricsAsync(state.CurrentTrack, reqId, _lyricsCts!.Token);
                _ = LoadCreditsAsync(state.CurrentTrack, reqId, _creditsCts!.Token);
            }
            else
            {
                IsCurrentTrackFavorite = false;
                LyricsState = LyricsState.Unavailable;
                SyncedLines = null;
                UnsyncedText = null;
                LyricsSource = null;
                CurrentLyricIndex = -1;
                IsLyricsPanelVisible = false;
                CurrentArtist = null;
                CurrentAlbum = null;
            }
        }
    }

    private static long _globalLyricsRequestId = 0;
    private long _activeLyricsRequestId = 0;
    private string? _lastLoadedTrackId = null;

    private async Task LoadLyricsAsync(Track track, long reqId, CancellationToken ct)
    {
        try
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (reqId != _activeLyricsRequestId || track.Id != CurrentTrack?.Id) return;
                LyricsState = LyricsState.Loading;
                IsLyricsPanelVisible = true;
                SyncedLines = null;
                UnsyncedText = null;
                LyricsSource = null;
                CurrentLyricIndex = -1;
                HasSyncedLyrics = false;
                HasPlainLyrics = false;
            });

            var fastData = await _lyricsService.GetLocalAndCachedLyricsAsync(track, ct);

            if (ct.IsCancellationRequested || reqId != _activeLyricsRequestId || track.Id != CurrentTrack?.Id)
            {
                return;
            }

            // Immediately apply FastPhase result to UI before background enrichment starts
            ApplyLyricsData(fastData, reqId, track);

            // If both representations are already available locally/cache, resolution is fully complete
            if (fastData.HasSyncedLyrics && fastData.HasPlainLyrics)
            {
                return;
            }

            var enrichedData = await _lyricsService.EnrichLyricsAsync(track, fastData, ct);

            if (ct.IsCancellationRequested || reqId != _activeLyricsRequestId || track.Id != CurrentTrack?.Id)
            {
                return;
            }

            // Update with enriched formats seamlessly
            ApplyLyricsData(enrichedData, reqId, track);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HTTP timeout
            System.Diagnostics.Debug.WriteLine($"[LYRICS] Req={reqId} | TrackId={track.Id} | HTTP Timeout: {ex.Message}");
            if (reqId == _activeLyricsRequestId && track.Id == CurrentTrack?.Id)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (reqId == _activeLyricsRequestId && track.Id == CurrentTrack?.Id)
                    {
                        LyricsState = LyricsState.NetworkUnavailable;
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation on track change / reload
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LYRICS] Req={reqId} | TrackId={track.Id} | Unexpected error: {ex.Message}");
            if (reqId == _activeLyricsRequestId && track.Id == CurrentTrack?.Id)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (reqId == _activeLyricsRequestId && track.Id == CurrentTrack?.Id && (LyricsState == LyricsState.Loading || LyricsState == LyricsState.Resolving))
                    {
                        LyricsState = LyricsState.Unavailable;
                        System.Diagnostics.Debug.WriteLine($"[LYRICS] Req={reqId} | Recovered from error to terminal state: Unavailable");
                    }
                });
            }
        }
        finally
        {
            // Terminal state safety net: If this is still the active request for the current track and state is still Loading/Resolving,
            // ensure it transitions out!
            if (reqId == _activeLyricsRequestId && track.Id == CurrentTrack?.Id)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (reqId == _activeLyricsRequestId && track.Id == CurrentTrack?.Id && (LyricsState == LyricsState.Loading || LyricsState == LyricsState.Resolving))
                    {
                        LyricsState = LyricsState.Unavailable;
                        System.Diagnostics.Debug.WriteLine($"[LYRICS] Req={reqId} | (finally) Transitioned to terminal state: Unavailable");
                    }
                });
            }
        }
    }

    [RelayCommand]
    public async Task ReloadLyricsAsync()
    {
        var track = CurrentTrack;
        if (track == null || IsReloadingLyrics) return;

        long reqId = Interlocked.Increment(ref _globalLyricsRequestId);
        _activeLyricsRequestId = reqId;
        _generationToken = reqId;

        var newCts = new CancellationTokenSource();
        var oldLyricsCts = Interlocked.Exchange(ref _lyricsCts, newCts);
        try
        {
            oldLyricsCts?.Cancel();
            oldLyricsCts?.Dispose();
        }
        catch { }

        var ct = newCts.Token;

        IsReloadingLyrics = true;
        LyricsState = LyricsState.Resolving;
        IsLyricsPanelVisible = true;

        try
        {
            var reloadedData = await _lyricsService.ReloadLyricsAsync(track, ct);
            if (reqId != _activeLyricsRequestId || track.Id != CurrentTrack?.Id)
            {
                return;
            }

            ApplyLyricsData(reloadedData, reqId, track);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (reqId == _activeLyricsRequestId && track.Id == CurrentTrack?.Id)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (reqId == _activeLyricsRequestId && track.Id == CurrentTrack?.Id)
                    {
                        LyricsState = LyricsState.NetworkUnavailable;
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation on track skip / new reload
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NowPlayingViewModel] ReloadLyricsAsync error: {ex.Message}");
            if (reqId == _activeLyricsRequestId && track.Id == CurrentTrack?.Id)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (reqId == _activeLyricsRequestId && track.Id == CurrentTrack?.Id)
                    {
                        LyricsState = LyricsState.NetworkUnavailable;
                    }
                });
            }
        }
        finally
        {
            if (reqId == _activeLyricsRequestId && track.Id == CurrentTrack?.Id)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    IsReloadingLyrics = false;
                    if (reqId == _activeLyricsRequestId && track.Id == CurrentTrack?.Id && (LyricsState == LyricsState.Loading || LyricsState == LyricsState.Resolving))
                    {
                        LyricsState = LyricsState.Unavailable;
                    }
                });
            }
        }
    }

    private void ApplyLyricsData(LyricsData data, long reqId, Track track)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (reqId != _activeLyricsRequestId || track.Id != CurrentTrack?.Id)
            {
                return;
            }

            _cachedLyricsData = data;
            HasSyncedLyrics = data.HasSyncedLyrics;
            HasPlainLyrics = data.HasPlainLyrics;
            SyncedLines = data.SyncedLines;
            UnsyncedText = data.PlainText;

            // Determine display mode: respect user selection for this track if valid, otherwise default to Synced then Static
            LyricDisplayMode targetMode;
            if (_userSelectedMode.HasValue)
            {
                if (_userSelectedMode.Value == LyricDisplayMode.Synced && data.HasSyncedLyrics)
                    targetMode = LyricDisplayMode.Synced;
                else if (_userSelectedMode.Value == LyricDisplayMode.Static && data.HasPlainLyrics)
                    targetMode = LyricDisplayMode.Static;
                else
                    targetMode = data.HasSyncedLyrics ? LyricDisplayMode.Synced : (data.HasPlainLyrics ? LyricDisplayMode.Static : LyricDisplayMode.Synced);
            }
            else
            {
                targetMode = data.HasSyncedLyrics ? LyricDisplayMode.Synced : (data.HasPlainLyrics ? LyricDisplayMode.Static : LyricDisplayMode.Synced);
            }

            SelectedLyricMode = targetMode;

            string? activeSource = (targetMode == LyricDisplayMode.Synced ? data.SyncedSource : data.StaticSource) ?? data.Source;
            LyricsSource = (data.HasSyncedLyrics || data.HasPlainLyrics) ? activeSource : null;

            if (data.HasSyncedLyrics || data.HasPlainLyrics)
            {
                IsLyricsPanelVisible = true;
                if (targetMode == LyricDisplayMode.Synced && data.HasSyncedLyrics)
                {
                    LyricsState = LyricsState.Synced;
                    UpdateLyricPosition(PositionSeconds);
                }
                else if (targetMode == LyricDisplayMode.Static && data.HasPlainLyrics)
                {
                    LyricsState = LyricsState.Unsynced;
                }
                else
                {
                    LyricsState = data.State;
                }
            }
            else
            {
                LyricsState = data.State;
                IsLyricsPanelVisible = true;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[LYRICS] Req={reqId} | UI State Applied: State={LyricsState}, Synced={HasSyncedLyrics}, Static={HasPlainLyrics}, Source={LyricsSource ?? "None"}, Mode={SelectedLyricMode}");
        });
    }

    [RelayCommand]
    public void SelectLyricMode(LyricDisplayMode mode)
    {
        _userSelectedMode = mode;
        SelectedLyricMode = mode;

        if (mode == LyricDisplayMode.Synced)
        {
            if (HasSyncedLyrics)
            {
                LyricsState = LyricsState.Synced;
                UpdateLyricPosition(PositionSeconds);
            }
        }
        else
        {
            if (HasPlainLyrics)
            {
                LyricsState = LyricsState.Unsynced;
            }
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
        catch (OperationCanceledException)
        {
            return;
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

        // UI-NP-06: apply the user's sync offset before comparing against line
        // timestamps. +500 ms => every line highlights 500 ms later.
        TimeSpan currentPos = TimeSpan.FromSeconds(currentSeconds - (LyricsOffsetMs / 1000.0));

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

        // O(log N) Binary Search when user seeked or position jumped.
        // TEST-04: the loop itself now lives in LyricsService.FindActiveLineIndex —
        // the one production implementation, covered by direct boundary tests.
        CurrentLyricIndex = Octave.Core.Services.Metadata.LyricsService.FindActiveLineIndex(SyncedLines, currentPos);
    }

    private bool _isRefreshingUpNextQueue;

    private void OnUpNextQueueChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_isRefreshingUpNextQueue) return;
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move)
        {
            var fullQueue = _queueService.GetCurrentQueue();
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
            int actualOldIdx = startIdx + e.OldStartingIndex;
            int actualNewIdx = startIdx + e.NewStartingIndex;
            _queueService.Reorder(actualOldIdx, actualNewIdx);
        }
    }

    private void RefreshUpNextQueue()
    {
        var fullQueue = _queueService.GetCurrentQueue();

        if (fullQueue == null || fullQueue.Count == 0)
        {
            _isRefreshingUpNextQueue = true;
            try
            {
                UpNextQueue.Clear();
            }
            finally
            {
                _isRefreshingUpNextQueue = false;
            }
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

        _isRefreshingUpNextQueue = true;
        try
        {
            UpNextQueue.Clear();
            for (int i = startIdx; i < fullQueue.Count; i++)
            {
                UpNextQueue.Add(fullQueue[i]);
            }
        }
        finally
        {
            _isRefreshingUpNextQueue = false;
        }

        // NP-19: the visible window was rebuilt - resolve artwork for rows that
        // don't have a token yet (Track itself carries none).
        HydrateQueueArtwork();

        IsQueuePanelVisible = UpNextQueue.Count > 0;
        MaybePrewarmNextTrackLyrics();
    }

    private void MaybePrewarmNextTrackLyrics()
    {
        if (_isDisposed) return;

        // Find next track in queue (item after current track)
        Track? nextTrack = null;
        if (UpNextQueue.Count > 1)
        {
            nextTrack = UpNextQueue[1].Track;
        }

        if (nextTrack == null || string.IsNullOrWhiteSpace(nextTrack.Id) ||
            nextTrack.Id == _lastPrewarmedTrackId || nextTrack.Id == CurrentTrack?.Id)
        {
            return;
        }

        _lastPrewarmedTrackId = nextTrack.Id;

        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _prewarmCts, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch { }

        var token = newCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _lyricsService.GetLyricsAsync(nextTrack, token);
                System.Diagnostics.Debug.WriteLine($"[LYRICS] Pre-warmed lyrics for Up-Next track: '{nextTrack.Title}' ({nextTrack.Id})");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LYRICS] Pre-warming failed for '{nextTrack.Title}': {ex.Message}");
            }
        }, token);
    }

    private void HydrateQueueArtwork()
    {
        List<(QueueItem Item, string AlbumId)>? pending = null;
        foreach (var item in UpNextQueue)
        {
            if (item.ArtworkUrl != null) continue;
            string albumId = item.Track.AlbumId;
            if (string.IsNullOrWhiteSpace(albumId)) continue;

            pending ??= new List<(QueueItem, string)>();
            pending.Add((item, albumId));
        }
        if (pending == null) return;

        _ = HydrateQueueArtworkAsync(pending);
    }

    private async Task HydrateQueueArtworkAsync(List<(QueueItem Item, string AlbumId)> pending)
    {
        try
        {
            var tokens = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (string albumId in pending.Select(p => p.AlbumId).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    var album = await _dbContext.GetAlbumByIdAsync(albumId);
                    tokens[albumId] = album?.ArtworkUrl ?? "";
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NowPlayingViewModel] Queue artwork lookup failed ({albumId}): {ex.Message}");
                }
            }

            if (tokens.Count == 0) return;

            // Empty-string tokens are cached too: they mean "album has no art",
            // and re-querying every state pulse for the same answer is churn.
            _dispatcher.TryEnqueue(() =>
            {
                if (_isDisposed) return;
                foreach (var (item, albumId) in pending)
                {
                    if (item.ArtworkUrl != null) continue;
                    if (tokens.TryGetValue(albumId, out string? token))
                    {
                        item.ArtworkUrl = token;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NowPlayingViewModel] Queue artwork hydration failed: {ex.Message}");
        }
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

    // UI-NP-02: per-row remove on the Now Playing queue panel. Id-based (not
    // RemoveAt): the panel shows a *window* of the full queue, so a row's
    // visible index is not the service-side index.
    [RelayCommand]
    public void RemoveQueueItem(QueueItem? item)
    {
        if (item == null) return;
        _queueService.RemoveById(item.Id);
    }

    // UI-NP-06: lyric sync nudge. ±500 ms per tap, clamped to ±5 s so a stuck
    // button can't walk the highlight into nonsense; re-evaluates the active
    // line immediately so the change is visible without waiting for the next tick.
    public void AdjustLyricsOffset(int deltaMs)
    {
        int clamped = Math.Clamp(LyricsOffsetMs + deltaMs, -5000, 5000);
        if (clamped == LyricsOffsetMs) return;
        LyricsOffsetMs = clamped;
        UpdateLyricPosition(PositionSeconds);
    }

    [RelayCommand]
    private void IncreaseLyricsOffset() => AdjustLyricsOffset(500);

    [RelayCommand]
    private void DecreaseLyricsOffset() => AdjustLyricsOffset(-500);

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _lyricsCts?.Cancel();
        _lyricsCts?.Dispose();
        _creditsCts?.Cancel();
        _creditsCts?.Dispose();
        var prewarmCts = Interlocked.Exchange(ref _prewarmCts, null);
        try
        {
            prewarmCts?.Cancel();
            prewarmCts?.Dispose();
        }
        catch { }
        UnsubscribeEvents();
    }
}
