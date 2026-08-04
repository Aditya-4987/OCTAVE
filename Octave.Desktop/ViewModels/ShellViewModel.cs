using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Audio;
using Octave.Core.Services.Database;
using Octave.Core.Services.Library;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Octave_Desktop.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly ILibraryService _libraryService;
    private readonly IQueueService _queueService;
    private readonly IAudioPlayerService _audioPlayer;
    private readonly ILibraryScanner _scanner;
    private readonly SqliteDbContext _dbContext;
    private readonly IPlaylistService _playlistService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    private CancellationTokenSource? _searchCts;
    public System.Collections.ObjectModel.ObservableCollection<SearchSuggestion> Suggestions { get; } = new();

    // Mirror of the current playback queue for the queue fly-out.
    public ObservableCollection<QueueItem> QueueItems { get; } = new();
    private bool _isRefreshingQueue;
    private string _lastQueueSignature = "";

    private string? _lastTrackId;
    private long _lastSeekSequenceToken = 0;

    [ObservableProperty]
    public partial string? CurrentArtworkUrl { get; set; }

    [ObservableProperty]
    public partial string TrackTitle { get; set; } = "Ready to ignite";

    [ObservableProperty]
    public partial string ArtistName { get; set; } = "Octave Core";

    [ObservableProperty]
    public partial string AlbumName { get; set; } = "";

    [ObservableProperty]
    public partial string StreamingQuality { get; set; } = "";
    
    [ObservableProperty]
    public partial string InfoBitrate { get; set; } = "";

    [ObservableProperty]
    public partial string InfoSampleRate { get; set; } = "";

    [ObservableProperty]
    public partial string InfoFileSize { get; set; } = "";

    [ObservableProperty]
    public partial string InfoFormat { get; set; } = "";

    [ObservableProperty]
    public partial string InfoLocation { get; set; } = "";

    [ObservableProperty]
    public partial bool IsNowPlayingOpen { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial double PositionSeconds { get; set; }

    [ObservableProperty]
    public partial double DurationSeconds { get; set; }

    public double Volume
    {
        get => _audioPlayer.Volume * 100.0;
        set
        {
            float targetVolume = (float)(value / 100.0);
            if (Math.Abs(_audioPlayer.Volume - targetVolume) > 0.001f)
            {
                _audioPlayer.Volume = targetVolume;
                OnPropertyChanged(nameof(Volume));
                OnPropertyChanged(nameof(IsMuted));
            }
        }
    }

    public bool IsMuted => _audioPlayer.IsMuted;

    [ObservableProperty]
    public partial bool IsShuffle { get; set; }

    [ObservableProperty]
    public partial RepeatMode RepeatMode { get; set; } = RepeatMode.None;

    [ObservableProperty]
    public partial string ConsoleOutput { get; set; } = "[System Ready]\n";

    [ObservableProperty]
    public partial bool IsDragging { get; set; }

    public ShellViewModel(
        ILibraryService libraryService,
        IQueueService queueService,
        IAudioPlayerService audioPlayer,
        ILibraryScanner scanner,
        SqliteDbContext dbContext,
        IPlaylistService playlistService)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _playlistService = playlistService ?? throw new ArgumentNullException(nameof(playlistService));

        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _queueService.PlaybackStateChanged += (s, state) =>
        {
            _dispatcher.TryEnqueue(() => UpdatePropertiesFromState(state));
        };
        
        _libraryService.FavoritesChanged += (s, e) =>
        {
            _ = RefreshFavoriteStatusAsync();
        };

        // High-frequency position ticks update only the timeline, not the whole
        // state - and are suppressed while the user is scrubbing the slider.
        _queueService.PositionChanged += (s, pos) =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (!IsDragging)
                {
                    PositionSeconds = pos;
                }
            });
        };

        _scanner.ScanProgressChanged += (s, args) =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                AppendConsole($"[Ingesting] {args.FilesProcessed} files... -> {Path.GetFileName(args.CurrentProcessingFile)}");
            });
        };

        // Build the equalizer bands from the engine's frequency table.
        var freqs = _audioPlayer.EqFrequencies;
        var gains = _audioPlayer.GetEqGains();
        for (int i = 0; i < freqs.Count; i++)
        {
            EqBands.Add(new EqBandViewModel(i, freqs[i], gains[i], (idx, g) => _audioPlayer.SetEqBand(idx, g)));
        }

        // Translate a user drag-reorder in the queue list into a queue operation.
        QueueItems.CollectionChanged += OnQueueItemsChanged;

        // Seed the queue mirror with anything already loaded (e.g. after resume).
        RefreshQueue();
    }

    private void OnQueueItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Ignore programmatic rebuilds; only react to user-initiated drag moves.
        if (_isRefreshingQueue) return;
        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            _queueService.Reorder(e.OldStartingIndex, e.NewStartingIndex);
        }
    }

    // Rebuilds the queue mirror only when the queue sequence actually changed, so
    // play/pause transitions don't reset the fly-out list.
    private void RefreshQueue()
    {
        var items = _queueService.GetCurrentQueue();
        string signature = string.Join("|", items.Select(i => i.Id));
        if (signature == _lastQueueSignature) return;
        _lastQueueSignature = signature;

        _isRefreshingQueue = true;
        try
        {
            QueueItems.Clear();
            foreach (var item in items)
            {
                QueueItems.Add(item);
            }
        }
        finally
        {
            _isRefreshingQueue = false;
        }
    }

    [RelayCommand]
    private void PlayQueueItem(QueueItem? item)
    {
        if (item == null) return;
        int index = QueueItems.IndexOf(item);
        if (index >= 0) _queueService.PlayIndex(index);
    }

    [RelayCommand]
    private void RemoveQueueItem(QueueItem? item)
    {
        if (item == null) return;
        int index = QueueItems.IndexOf(item);
        if (index >= 0) _queueService.RemoveAt(index);
    }

    [RelayCommand]
    private void ClearQueue() => _queueService.Clear();

    [RelayCommand]
    private void ToggleNowPlaying() => IsNowPlayingOpen = !IsNowPlayingOpen;

    // ---- Context Menu Commands (Global) -----------------------------------

    [RelayCommand]
    private void PlayNext(Track? track)
    {
        if (track != null) _queueService.EnqueueNext(track);
    }

    [RelayCommand]
    private void AddToQueue(Track? track)
    {
        if (track != null) _queueService.Enqueue(track);
    }

    [RelayCommand]
    private async Task ToggleFavorite(Track? track)
    {
        if (track != null)
        {
            await _libraryService.ToggleFavoriteAsync(track.Id);
        }
    }

    [RelayCommand]
    private async Task ToggleCurrentTrackFavorite()
    {
        if (_lastKnownTrackId != null)
        {
            await _libraryService.ToggleFavoriteAsync(_lastKnownTrackId);
        }
    }

    [RelayCommand]
    private async Task AddToPlaylist(object parameter)
    {
        // Parameter expected as string "playlistId|trackId"
        if (parameter is string payload && payload.Contains('|'))
        {
            var parts = payload.Split('|');
            await _playlistService.AddTrackAsync(parts[0], parts[1]);
        }
    }

    [RelayCommand]
    private async Task RemoveFromPlaylist(object parameter)
    {
        // Parameter expected as string "playlistId|trackId"
        if (parameter is string payload && payload.Contains('|'))
        {
            var parts = payload.Split('|');
            await _playlistService.RemoveTrackAsync(parts[0], parts[1]);
        }
    }

    public ObservableCollection<Playlist> AvailablePlaylists { get; } = new();

    public async Task LoadAvailablePlaylistsAsync()
    {
        var lists = await _playlistService.GetPlaylistsAsync();
        _dispatcher.TryEnqueue(() =>
        {
            AvailablePlaylists.Clear();
            foreach (var p in lists) AvailablePlaylists.Add(p);
        });
    }

    // ---- Music folder management ------------------------------------------

    public ObservableCollection<string> MonitoredFolders { get; } = new();

    public async Task LoadFoldersAsync()
    {
        var folders = await _libraryService.GetMonitoredFoldersAsync();
        _dispatcher.TryEnqueue(() =>
        {
            MonitoredFolders.Clear();
            foreach (var f in folders) MonitoredFolders.Add(f);
        });
    }

    // Called from the Settings page after the folder picker resolves.
    public async Task AddFolderAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        IsProcessing = true;
        AppendConsole($"[Folders] Scanning new folder: {path}");
        try
        {
            await _libraryService.AddFolderAsync(path, CancellationToken.None);
            await LoadFoldersAsync();
            AppendConsole($"[Folders] Added: {path}");
        }
        catch (Exception ex)
        {
            AppendConsole($"[Folders] Add failed: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task RemoveFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await _libraryService.RemoveFolderAsync(path);
            await LoadFoldersAsync();
            AppendConsole($"[Folders] Removed (restart to fully stop watching): {path}");
        }
        catch (Exception ex)
        {
            AppendConsole($"[Folders] Remove failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RescanAll()
    {
        IsProcessing = true;
        AppendConsole("[Folders] Rescanning all folders...");
        try
        {
            await _libraryService.RescanAllAsync(CancellationToken.None);
            AppendConsole("[Folders] Rescan complete.");
        }
        catch (Exception ex)
        {
            AppendConsole($"[Folders] Rescan failed: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    // ---- Sleep timer ------------------------------------------------------

    private Timer? _sleepTimer;

    [ObservableProperty]
    public partial string SleepTimerStatus { get; set; } = "Off";

    [RelayCommand]
    private void SetSleepTimer(string? minutesText)
    {
        _sleepTimer?.Dispose();
        _sleepTimer = null;

        if (!int.TryParse(minutesText, out int minutes) || minutes <= 0)
        {
            SleepTimerStatus = "Off";
            return;
        }

        SleepTimerStatus = $"Pausing in {minutes} min";
        _sleepTimer = new Timer(_ =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                _queueService.Pause();
                SleepTimerStatus = "Off";
                _sleepTimer?.Dispose();
                _sleepTimer = null;
            });
        }, null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
    }

    // ---- Equalizer --------------------------------------------------------

    public ObservableCollection<EqBandViewModel> EqBands { get; } = new();

    public bool EqEnabled
    {
        get => _audioPlayer.IsEqEnabled;
        set
        {
            _audioPlayer.SetEqEnabled(value);
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void EqPreset(string? preset)
    {
        // Gain values (dB) per band for each preset, low -> high frequency.
        double[] gains = preset switch
        {
            "BassBoost" => new double[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 },
            "TrebleBoost" => new double[] { 0, 0, 0, 0, 0, 0, 2, 4, 5, 6 },
            "Vocal" => new double[] { -2, -1, 0, 2, 4, 4, 3, 1, 0, -1 },
            "Electronic" => new double[] { 4, 3, 0, -2, -3, -3, -1, 2, 4, 5 },
            "Acoustic" => new double[] { 3, 4, 3, 1, 1, 1, 2, 2, 1, 0 },
            _ => new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } // Flat
        };

        for (int i = 0; i < EqBands.Count && i < gains.Length; i++)
        {
            EqBands[i].Gain = gains[i]; // setter applies to the engine
        }
    }

    // ---- Duplicate detection ----------------------------------------------

    public ObservableCollection<DuplicateGroup> Duplicates { get; } = new();

    [ObservableProperty]
    public partial string DuplicatesSummary { get; set; } = "";
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotProcessing))]
    public partial bool IsProcessing { get; set; }

    public bool IsNotProcessing => !IsProcessing;

    [RelayCommand]
    private async Task FindDuplicates()
    {
        var groups = await _libraryService.GetDuplicatesAsync();
        _dispatcher.TryEnqueue(() =>
        {
            Duplicates.Clear();
            foreach (var g in groups) Duplicates.Add(g);
            DuplicatesSummary = groups.Count == 0
                ? "No duplicates found."
                : $"{groups.Count} duplicate group(s) found.";
        });
    }

    [RelayCommand]
    private async Task DeleteAllDuplicates()
    {
        var snapshot = System.Linq.Enumerable.ToList(Duplicates);
        foreach (var group in snapshot)
        {
            // Delete all except the very first track in the duplicate group
            for (int i = 1; i < group.Tracks.Count; i++)
            {
                try
                {
                    var track = group.Tracks[i];
                    Octave.Core.Helpers.ShellRecycleBin.SendToRecycleBin(track.SourceUri);
                    await _libraryService.DeleteTrackAsync(track.Id);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to delete duplicate {group.Tracks[i].SourceUri}: {ex.Message}");
                }
            }
        }
        await FindDuplicates();
    }

    private const int MaxConsoleChars = 8000;

    // Prepends a line to the console log and caps total length so a large scan
    // (a progress line every 25 files) can't grow the string without bound.
    private void AppendConsole(string line)
    {
        string combined = line + "\n" + ConsoleOutput;
        if (combined.Length > MaxConsoleChars)
        {
            combined = combined.Substring(0, MaxConsoleChars);
        }
        ConsoleOutput = combined;
    }
    
    [ObservableProperty]
    public partial bool IsCurrentTrackFavorite { get; set; }
    
    private string? _lastKnownTrackId;

    private async Task RefreshFavoriteStatusAsync()
    {
        if (_lastKnownTrackId != null)
        {
            var isFav = await _libraryService.IsFavoriteAsync(_lastKnownTrackId);
            _dispatcher.TryEnqueue(() => IsCurrentTrackFavorite = isFav);
        }
        else
        {
            _dispatcher.TryEnqueue(() => IsCurrentTrackFavorite = false);
        }
    }

    private void UpdatePropertiesFromState(PlaybackState state)
    {
        if (state.SequenceToken < _lastSeekSequenceToken)
        {
            return;
        }

        if (state.CurrentTrack != null)
        {
            _lastKnownTrackId = state.CurrentTrack.Id;
            _ = RefreshFavoriteStatusAsync();
            TrackTitle = state.CurrentTrack.Title;
            ArtistName = state.CurrentTrack.ArtistName;
            AlbumName = state.CurrentTrack.AlbumTitle;
            StreamingQuality = _audioPlayer.StreamingQuality;
            
            try
            {
                var uri = state.CurrentTrack.SourceUri;
                InfoLocation = uri;
                InfoFormat = Path.GetExtension(uri).TrimStart('.').ToUpperInvariant();
                
                if (File.Exists(uri))
                {
                    var fileInfo = new FileInfo(uri);
                    InfoFileSize = $"{(fileInfo.Length / (1024.0 * 1024.0)):0.00} MB";

                    using var tfile = TagLib.File.Create(uri);
                    if (tfile.Properties != null)
                    {
                        InfoBitrate = $"{tfile.Properties.AudioBitrate} kbps";
                        InfoSampleRate = $"{tfile.Properties.AudioSampleRate} Hz";
                    }
                }
            }
            catch
            {
                InfoFileSize = "Unknown";
                InfoBitrate = "Unknown";
                InfoSampleRate = "Unknown";
            }
        }
        else
        {
            TrackTitle = "No Track Loaded";
            ArtistName = "Unknown Artist";
            AlbumName = "";
            StreamingQuality = "";
            InfoLocation = "";
            InfoFormat = "";
            InfoFileSize = "";
            InfoBitrate = "";
            InfoSampleRate = "";
        }

        if (state.CurrentTrack?.Id != _lastTrackId)
        {
            _lastTrackId = state.CurrentTrack?.Id;
            
            if (state.CurrentTrack == null)
            {
                CurrentArtworkUrl = null;
            }
            else
            {
                _ = LoadArtworkAsync(state.CurrentTrack.AlbumId);
            }
        }

        IsPlaying = state.Status == PlaybackStatus.Playing;
        if (!IsDragging)
        {
            PositionSeconds = state.PositionSeconds;
        }
        DurationSeconds = state.DurationSeconds;
        IsShuffle = state.IsShuffle;
        RepeatMode = state.RepeatMode;
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(IsMuted));

        RefreshQueue();
    }

    private async Task LoadArtworkAsync(string albumId)
    {
        var album = await _libraryService.GetAlbumByIdAsync(albumId);
        _dispatcher.TryEnqueue(() =>
        {
            CurrentArtworkUrl = album?.ArtworkUrl;
        });
    }
    [RelayCommand]
    private void Play() => _queueService.Resume();

    [RelayCommand]
    private void Pause() => _queueService.Pause();

    [RelayCommand]
    private void ToggleMute()
    {
        _audioPlayer.ToggleMute();
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(IsMuted));
    }

    [RelayCommand]
    private void SeekPlayback(double targetedSeconds)
    {
        double clamped = Math.Clamp(targetedSeconds, 0, DurationSeconds);
        var newState = _queueService.Seek(clamped);
        _lastSeekSequenceToken = newState.SequenceToken;
        UpdatePropertiesFromState(newState);
        IsDragging = false;
    }

    [RelayCommand]
    private void Next() => _queueService.PlayNext();

    [RelayCommand]
    private void Previous() => _queueService.PlayPrevious();

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (IsPlaying) _queueService.Pause();
        else _queueService.Resume();
    }

    [RelayCommand]
    private void VolumeUp() => Volume = Math.Min(100, Volume + 5);

    [RelayCommand]
    private void VolumeDown() => Volume = Math.Max(0, Volume - 5);

    [RelayCommand]
    private void ToggleShuffle() => _queueService.SetShuffle(!IsShuffle);

    [RelayCommand]
    private void CycleRepeat()
    {
        var nextMode = RepeatMode switch
        {
            RepeatMode.None => RepeatMode.Track,
            RepeatMode.Track => RepeatMode.Queue,
            RepeatMode.Queue => RepeatMode.None,
            _ => RepeatMode.None
        };
        _queueService.SetRepeatMode(nextMode);
    }

    [RelayCommand]
    private async Task RunStaticFireAsync()
    {
        ConsoleOutput = "[Static Fire] Crawling MyMusic Vault...\n" + ConsoleOutput;

        try
        {
            ConsoleOutput = "[Initializing SQLite DDL Vault...]\n" + ConsoleOutput;
            await _dbContext.InitializeAsync();
            ConsoleOutput = "[Database initialized successfully]\n" + ConsoleOutput;

            string musicPath = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            ConsoleOutput = $"[Harness] Traversing special music folder: {musicPath}\n" + ConsoleOutput;

            await _libraryService.ScanLocalLibraryAsync(musicPath, CancellationToken.None);

            int count = await _libraryService.GetTotalTrackCountAsync();
            ConsoleOutput = $"[Harness] Scanning finished. Total database tracks: {count}\n" + ConsoleOutput;

            if (count > 0)
            {
                var tracks = await _libraryService.GetAllTracksAsync();
                _queueService.Clear();
                _queueService.EnqueueRange(tracks);

                AppendConsole($"[Harness] Pushed {tracks.Count} tracks to play queue. Activating track at index 0...");
                _queueService.PlayIndex(0);
            }
            else
            {
                ConsoleOutput = "[Harness] Warn: No local tracks found in special music folder to play.\n" + ConsoleOutput;
            }

            ConsoleOutput = "[Harness] Static fire launchpad sequence complete.\n" + ConsoleOutput;
        }
        catch (Exception ex)
        {
            ConsoleOutput = $"[Harness] FAILED: {ex.Message}\n" + ConsoleOutput;
        }
    }

    public async Task UpdateSearchSuggestionsAsync(string query)
    {
        if (_searchCts != null)
        {
            try
            {
                _searchCts.Cancel();
            }
            catch (ObjectDisposedException) { }
            _searchCts.Dispose();
            _searchCts = null;
        }

        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            _dispatcher.TryEnqueue(() => Suggestions.Clear());
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;

        try
        {
            await Task.Delay(300, token);

            var results = await _libraryService.SearchLibraryAsync(query, limit: 8);

            if (token.IsCancellationRequested) return;

            _dispatcher.TryEnqueue(() =>
            {
                Suggestions.Clear();

                foreach (var track in results.Tracks)
                {
                    Suggestions.Add(new SearchSuggestion(track.Title, EntityType.Track, track.Id));
                }

                foreach (var album in results.Albums)
                {
                    Suggestions.Add(new SearchSuggestion(album.Title, EntityType.Album, album.Id));
                }

                foreach (var artist in results.Artists)
                {
                    Suggestions.Add(new SearchSuggestion(artist.Name, EntityType.Artist, artist.Id));
                }

                if (results.Playlists != null)
                {
                    foreach (var playlist in results.Playlists)
                    {
                        Suggestions.Add(new SearchSuggestion(playlist.Title, EntityType.Playlist, playlist.Id));
                    }
                }
            });
        }
        catch (TaskCanceledException)
        {
            // Suppress cancellations
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Search Suggestions] Error: {ex}");
        }
    }

    public async Task PlayTrackByIdAsync(string trackId)
    {
        var track = await _libraryService.GetTrackByIdAsync(trackId);
        if (track != null)
        {
            _dispatcher.TryEnqueue(() =>
            {
                _queueService.Clear();
                _queueService.Enqueue(track);
                _queueService.PlayIndex(0);
            });
        }
    }
}
