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
    private readonly IArtworkCacheManager? _artworkCacheManager;
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
    public partial string OutputDeviceQuality { get; set; } = "";

    [ObservableProperty]
    public partial string OutputDeviceName { get; set; } = "";

    [ObservableProperty]
    public partial Octave.Core.Models.AudioQualityDetails? AudioQualityInfo { get; set; }
    
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
    public partial string InfoYear { get; set; } = "";

    [ObservableProperty]
    public partial string InfoGenre { get; set; } = "";

    [ObservableProperty]
    public partial string InfoTrackNumber { get; set; } = "";

    [ObservableProperty]
    public partial string InfoDiscNumber { get; set; } = "";

    [ObservableProperty]
    public partial string InfoReplayGain { get; set; } = "";

    [ObservableProperty]
    public partial string InfoChannels { get; set; } = "";

    [ObservableProperty]
    public partial string InfoBitDepth { get; set; } = "";

    [ObservableProperty]
    public partial bool IsNowPlayingOpen { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

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
                if (safe <= 0 || _positionSeconds > safe)
                {
                    PositionSeconds = (safe <= 0) ? 0.0 : safe;
                }
            }
        }
    }

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
        IPlaylistService playlistService,
        IArtworkCacheManager? artworkCacheManager = null)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _playlistService = playlistService ?? throw new ArgumentNullException(nameof(playlistService));
        _artworkCacheManager = artworkCacheManager;

        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _ = LoadSettingsAsync();

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

        // Build the equalizer bands from the engine's frequency table. VM-12:
        // construction no longer touches the engine - each band's restored gain
        // is pushed explicitly once the list is complete.
        var freqs = _audioPlayer.EqFrequencies;
        var gains = _audioPlayer.GetEqGains();
        for (int i = 0; i < freqs.Count; i++)
        {
            EqBands.Add(new EqBandViewModel(i, freqs[i], gains[i], (idx, g) => _audioPlayer.SetEqBand(idx, g)));
        }
        foreach (var band in EqBands)
        {
            band.PushInitialGainToEngine();
            // UI-ST-03: every gain change - slider drag or preset apply -
            // recomputes the active-preset indicator and persists gains.
            band.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EqBandViewModel.Gain))
                {
                    UpdateActivePresetName();
                    SaveEqGains();
                }
            };
        }
        UpdateActivePresetName();

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
            _lastQueueSignature = string.Join("|", QueueItems.Select(i => i.Id));
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
            HydrateQueueArtwork();
        }
        finally
        {
            _isRefreshingQueue = false;
        }
    }

    private void HydrateQueueArtwork()
    {
        List<(QueueItem Item, string AlbumId)>? pending = null;
        foreach (var item in QueueItems)
        {
            if (item.ArtworkUrl != null) continue;
            string albumId = item.Track.AlbumId;
            if (string.IsNullOrWhiteSpace(albumId)) continue;

            pending ??= new List<(QueueItem, string)>();
            pending.Add((item, albumId));
        }

        if (pending == null || pending.Count == 0) return;
        _ = HydrateQueueArtworkAsync(pending);
    }

    private async Task HydrateQueueArtworkAsync(List<(QueueItem Item, string AlbumId)> pending)
    {
        var albumIds = pending.Select(p => p.AlbumId).Distinct().ToList();
        var albumMap = new Dictionary<string, string?>();

        foreach (var albumId in albumIds)
        {
            try
            {
                var album = await _libraryService.GetAlbumByIdAsync(albumId);
                albumMap[albumId] = album?.ArtworkUrl;
            }
            catch
            {
                albumMap[albumId] = null;
            }
        }

        _dispatcher.TryEnqueue(() =>
        {
            foreach (var (item, albumId) in pending)
            {
                if (albumMap.TryGetValue(albumId, out var artUrl) && !string.IsNullOrEmpty(artUrl))
                {
                    item.ArtworkUrl = artUrl;
                }
            }
        });
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

    [RelayCommand]
    public async Task ClearDatabaseAsync()
    {
        IsProcessing = true;
        AppendConsole("[Database] Clearing entire library database and caches...");
        try
        {
            // 1. Stop playback and clear the active/unshuffled queue
            _audioPlayer.Stop();
            _queueService.Clear(keepCurrentTrack: false);

            // 2. Wipe all library database tables and monitored folders
            await _libraryService.ClearDatabaseAsync(preserveSettings: false);

            // 3. Clear disk artwork cache files
            if (_artworkCacheManager != null)
            {
                await _artworkCacheManager.ClearCacheAsync();
            }

            // 5. Reload monitored folders in UI
            await LoadFoldersAsync();

            // 6. Reset duplicates collection and summary
            _dispatcher.TryEnqueue(() =>
            {
                Duplicates.Clear();
                DuplicatesSummary = "";
            });

            AppendConsole("[Database] Library database and all caches cleared successfully.");
        }
        catch (Exception ex)
        {
            AppendConsole($"[Database] Failed to clear database: {ex.Message}");
            throw;
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

        // SHELL-01: capture the instance this callback belongs to. A stale
        // timer's already-queued callback used to run against the FIELD - it
        // paused playback and then disposed the user's freshly-set replacement
        // timer. Only the still-current instance may act.
        Timer? self = null;
        self = new Timer(_ =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (!ReferenceEquals(self, _sleepTimer)) return;

                _queueService.Pause();
                SleepTimerStatus = "Off";
                _sleepTimer?.Dispose();
                _sleepTimer = null;
            });
        }, null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
        _sleepTimer = self;
    }

    // ---- Playback Resume Settings ------------------------------------------

    public bool RestorePositionOnStartup
    {
        get => _queueService.RestorePositionOnStartup;
        set
        {
            if (_queueService.RestorePositionOnStartup != value)
            {
                _queueService.RestorePositionOnStartup = value;
                _ = _dbContext.SetSettingAsync("RestorePositionOnStartup", value.ToString());
                OnPropertyChanged();
            }
        }
    }

    private bool _isVisualizerEnabled = true;
    public bool IsVisualizerEnabled
    {
        get => _isVisualizerEnabled;
        set
        {
            if (_isVisualizerEnabled != value)
            {
                _isVisualizerEnabled = value;
                _ = _dbContext.SetSettingAsync("IsVisualizerEnabled", value.ToString());
                OnPropertyChanged();
            }
        }
    }

    // ---- Crossfade Settings ------------------------------------------------

    public bool IsCrossfadeEnabled
    {
        get => _audioPlayer.CrossfadeDurationMs > 0;
        set
        {
            _audioPlayer.CrossfadeDurationMs = value ? (CrossfadeSeconds * 1000) : 0;
            _ = _dbContext.SetSettingAsync("IsCrossfadeEnabled", value.ToString());
            OnPropertyChanged();
            OnPropertyChanged(nameof(CrossfadeSeconds));
        }
    }

    private int _crossfadeSeconds = 1; // Default 1 second
    public int CrossfadeSeconds
    {
        get => _audioPlayer.CrossfadeDurationMs > 0 ? (_audioPlayer.CrossfadeDurationMs / 1000) : _crossfadeSeconds;
        set
        {
            _crossfadeSeconds = Math.Clamp(value, 1, 10);
            if (IsCrossfadeEnabled)
            {
                _audioPlayer.CrossfadeDurationMs = _crossfadeSeconds * 1000;
            }
            _ = _dbContext.SetSettingAsync("CrossfadeSeconds", _crossfadeSeconds.ToString());
            OnPropertyChanged();
        }
    }

    // ---- Equalizer --------------------------------------------------------

    public ObservableCollection<EqBandViewModel> EqBands { get; } = new();

    // UI-ST-03: single source of truth for preset gain curves, so the active-
    // preset indicator compares against exactly what applying a preset writes.
    private static readonly Dictionary<string, double[]> PresetGains = new()
    {
        ["Flat"]        = new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        ["BassBoost"]   = new double[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 },
        ["TrebleBoost"] = new double[] { 0, 0, 0, 0, 0, 0, 2, 4, 5, 6 },
        ["Vocal"]       = new double[] { -2, -1, 0, 2, 4, 4, 3, 1, 0, -1 },
        ["Electronic"]  = new double[] { 4, 3, 0, -2, -3, -3, -1, 2, 4, 5 },
        ["Acoustic"]    = new double[] { 3, 4, 3, 1, 1, 1, 2, 2, 1, 0 },
    };

    // UI-ST-03: which preset currently matches the band gains ("Custom" once the
    // user hand-tunes anything). Derived from actual values, so a restored
    // session shows its true state without extra plumbing.
    [ObservableProperty]
    public partial string ActivePresetName { get; set; } = "";

    // UI-ST-04: lets the Settings page show whether bass_fx.dll actually loaded.
    public bool IsEqEngineAvailable => _audioPlayer.IsEqEngineAvailable;

    public bool EqEnabled
    {
        get => _audioPlayer.IsEqEnabled;
        set
        {
            if (_audioPlayer.IsEqEnabled != value)
            {
                _audioPlayer.SetEqEnabled(value);
                _ = _dbContext.SetSettingAsync("EqEnabled", value.ToString());
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private void EqPreset(string? preset)
    {
        double[] gains = PresetGains.GetValueOrDefault(preset ?? "", PresetGains["Flat"]);

        for (int i = 0; i < EqBands.Count && i < gains.Length; i++)
        {
            EqBands[i].Gain = gains[i]; // setter applies to the engine (and raises
                                        // the PropertyChanged that refreshes ActivePresetName and saves gains)
        }
    }

    private void UpdateActivePresetName()
    {
        if (EqBands.Count == 0) return;

        foreach (var (name, gains) in PresetGains)
        {
            bool match = gains.Length == EqBands.Count;
            for (int i = 0; match && i < gains.Length; i++)
            {
                match = Math.Abs(EqBands[i].Gain - gains[i]) <= 0.01;
            }
            if (match)
            {
                ActivePresetName = name;
                return;
            }
        }
        ActivePresetName = "Custom";
    }

    private void SaveEqGains()
    {
        try
        {
            var gainsStr = string.Join(",", EqBands.Select(b => b.Gain.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
            _ = _dbContext.SetSettingAsync("EqGains", gainsStr);
        }
        catch { }
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            // Visualizer
            var visVal = await _dbContext.GetSettingAsync("IsVisualizerEnabled");
            if (bool.TryParse(visVal, out var visBool))
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _isVisualizerEnabled = visBool;
                    OnPropertyChanged(nameof(IsVisualizerEnabled));
                });
            }

            // RestorePositionOnStartup
            var resVal = await _dbContext.GetSettingAsync("RestorePositionOnStartup");
            if (bool.TryParse(resVal, out var resBool))
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _queueService.RestorePositionOnStartup = resBool;
                    OnPropertyChanged(nameof(RestorePositionOnStartup));
                });
            }

            // Crossfade
            var cfEnabledVal = await _dbContext.GetSettingAsync("IsCrossfadeEnabled");
            var cfSecVal = await _dbContext.GetSettingAsync("CrossfadeSeconds");
            int cfSeconds = 1;
            if (int.TryParse(cfSecVal, out var s))
            {
                cfSeconds = Math.Clamp(s, 1, 10);
            }
            bool cfEnabled = false;
            if (bool.TryParse(cfEnabledVal, out var cfB))
            {
                cfEnabled = cfB;
            }

            _dispatcher.TryEnqueue(() =>
            {
                _crossfadeSeconds = cfSeconds;
                _audioPlayer.CrossfadeDurationMs = cfEnabled ? (cfSeconds * 1000) : 0;
                OnPropertyChanged(nameof(IsCrossfadeEnabled));
                OnPropertyChanged(nameof(CrossfadeSeconds));
            });

            // Equalizer
            var eqEnabledVal = await _dbContext.GetSettingAsync("EqEnabled");
            if (bool.TryParse(eqEnabledVal, out var eqB))
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _audioPlayer.SetEqEnabled(eqB);
                    OnPropertyChanged(nameof(EqEnabled));
                });
            }

            var eqGainsVal = await _dbContext.GetSettingAsync("EqGains");
            if (!string.IsNullOrEmpty(eqGainsVal))
            {
                var parts = eqGainsVal.Split(',');
                _dispatcher.TryEnqueue(() =>
                {
                    for (int i = 0; i < parts.Length && i < EqBands.Count; i++)
                    {
                        if (double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g))
                        {
                            EqBands[i].Gain = g;
                        }
                    }
                    UpdateActivePresetName();
                });
            }
        }
        catch { }
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
                    bool recycled = Octave.Core.Helpers.ShellRecycleBin.SendToRecycleBin(track.SourceUri);
                    if (recycled)
                    {
                        await _libraryService.DeleteTrackAsync(track.Id);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[ShellViewModel] Skipped deleting track {track.Id}; file could not be recycled: {track.SourceUri}");
                    }
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
            OutputDeviceQuality = _audioPlayer.OutputDeviceQuality;
            OutputDeviceName = _audioPlayer.OutputDeviceName;
            AudioQualityInfo = _audioPlayer.QualityDetails;
            
            var uri = state.CurrentTrack.SourceUri;
            InfoLocation = uri;
            InfoFormat = Path.GetExtension(uri).TrimStart('.').ToUpperInvariant();
            InfoYear = state.CurrentTrack.Year > 0 ? state.CurrentTrack.Year.ToString() : "-";
            InfoGenre = !string.IsNullOrWhiteSpace(state.CurrentTrack.Genre) ? state.CurrentTrack.Genre : "-";
            InfoTrackNumber = state.CurrentTrack.TrackNumber > 0 ? state.CurrentTrack.TrackNumber.ToString() : "-";
            InfoDiscNumber = state.CurrentTrack.DiscNumber > 0 ? state.CurrentTrack.DiscNumber.ToString() : "-";
            InfoReplayGain = Math.Abs(state.CurrentTrack.ReplayGain) > 0.001f ? $"{state.CurrentTrack.ReplayGain:+0.00;-0.00} dB" : "-";
            InfoChannels = _audioPlayer.QualityDetails?.ChannelsText ?? "Stereo (2.0)";
            InfoBitDepth = _audioPlayer.QualityDetails?.BitDepth > 0 ? $"{_audioPlayer.QualityDetails.BitDepth}-bit" : "-";

            if (state.CurrentTrack.Id != _lastTrackId)
            {
                InfoFileSize = "Loading...";
                InfoBitrate = "Loading...";
                InfoSampleRate = "Loading...";
                _ = LoadTrackFileMetadataAsync(uri, state.CurrentTrack.Id);
            }
        }
        else
        {
            TrackTitle = "No Track Loaded";
            ArtistName = "Unknown Artist";
            AlbumName = "";
            StreamingQuality = "";
            OutputDeviceQuality = "";
            OutputDeviceName = "";
            AudioQualityInfo = null;
            InfoLocation = "";
            InfoFormat = "";
            InfoFileSize = "";
            InfoBitrate = "";
            InfoSampleRate = "";
            InfoYear = "";
            InfoGenre = "";
            InfoTrackNumber = "";
            InfoDiscNumber = "";
            InfoReplayGain = "";
            InfoChannels = "";
            InfoBitDepth = "";
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
        DurationSeconds = state.DurationSeconds > 0 ? state.DurationSeconds : (state.CurrentTrack?.DurationSeconds ?? 0);
        if (!IsDragging)
        {
            PositionSeconds = state.PositionSeconds;
        }
        IsShuffle = state.IsShuffle;
        RepeatMode = state.RepeatMode;
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(IsMuted));

        RefreshQueue();
    }

    private async Task LoadTrackFileMetadataAsync(string uri, string trackId)
    {
        if (string.IsNullOrWhiteSpace(uri) || !File.Exists(uri))
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_lastTrackId == trackId)
                {
                    InfoFileSize = "Unknown";
                    InfoBitrate = "Unknown";
                    InfoSampleRate = "Unknown";
                }
            });
            return;
        }

        try
        {
            var (fileSize, bitrate, sampleRate) = await Task.Run(() =>
            {
                var fileInfo = new FileInfo(uri);
                string size = $"{(fileInfo.Length / (1024.0 * 1024.0)):0.00} MB";
                string br = "Unknown";
                string sr = "Unknown";

                try
                {
                    using var tfile = TagLib.File.Create(uri);
                    if (tfile.Properties != null)
                    {
                        br = $"{tfile.Properties.AudioBitrate} kbps";
                        sr = $"{tfile.Properties.AudioSampleRate} Hz";
                    }
                }
                catch { }

                return (size, br, sr);
            });

            _dispatcher.TryEnqueue(() =>
            {
                if (_lastTrackId == trackId)
                {
                    InfoFileSize = fileSize;
                    InfoBitrate = bitrate;
                    InfoSampleRate = sampleRate;
                }
            });
        }
        catch
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_lastTrackId == trackId)
                {
                    InfoFileSize = "Unknown";
                    InfoBitrate = "Unknown";
                    InfoSampleRate = "Unknown";
                }
            });
        }
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
        // SH-02: rapid TextChanged ticks run this concurrently - the old
        // cancel/dispose/null sequence on the shared field interleaved into
        // double-dispose crashes and one caller's fresh source being cancelled
        // by the next. Atomically take ownership of whichever source is current.
        var previous = Interlocked.Exchange(ref _searchCts, null);
        if (previous != null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
            try { previous.Dispose(); } catch (ObjectDisposedException) { }
        }

        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            _dispatcher.TryEnqueue(() => Suggestions.Clear());
            return;
        }

        var cts = new CancellationTokenSource();

        // Install ours; anything another caller slipped in between is older
        // than this keystroke and gets superseded (newest-install-wins).
        Interlocked.Exchange(ref _searchCts, cts)?.Cancel();
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
