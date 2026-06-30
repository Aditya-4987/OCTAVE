using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Audio;
using Octave.Core.Services.Database;
using Octave.Core.Services.Library;
using System;
using System.IO;
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
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    private CancellationTokenSource? _searchCts;
    public System.Collections.ObjectModel.ObservableCollection<SearchSuggestion> Suggestions { get; } = new();

    private string? _lastTrackId;
    private long _lastSeekSequenceToken = 0;

    [ObservableProperty]
    private string? _currentArtworkUrl;

    [ObservableProperty]
    private string _trackTitle = "Ready to ignite";

    [ObservableProperty]
    private string _artistName = "Octave Core";

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private float _volume = 1.0f;

    [ObservableProperty]
    private bool _isShuffle;

    [ObservableProperty]
    private RepeatMode _repeatMode = RepeatMode.None;

    [ObservableProperty]
    private string _consoleOutput = "[System Ready]\n";

    [ObservableProperty]
    private bool _isDragging;

    public ShellViewModel(
        ILibraryService libraryService,
        IQueueService queueService,
        IAudioPlayerService audioPlayer,
        ILibraryScanner scanner,
        SqliteDbContext dbContext)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _queueService.PlaybackStateChanged += (s, state) =>
        {
            _dispatcher.TryEnqueue(() => UpdatePropertiesFromState(state));
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
    private void UpdatePropertiesFromState(PlaybackState state)
    {
        if (state.SequenceToken < _lastSeekSequenceToken)
        {
            return;
        }

        if (state.CurrentTrack != null)
        {
            TrackTitle = state.CurrentTrack.Title;
            ArtistName = state.CurrentTrack.ArtistName;
        }
        else
        {
            TrackTitle = "No Track Loaded";
            ArtistName = "Unknown Artist";
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
        Volume = state.Volume;
        IsShuffle = state.IsShuffle;
        RepeatMode = state.RepeatMode;
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
