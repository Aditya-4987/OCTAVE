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
    private readonly LocalLibraryScanner _scanner;
    private readonly SqliteDbContext _dbContext;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

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
        LocalLibraryScanner scanner,
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

        _scanner.ScanProgressChanged += (s, args) =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                ConsoleOutput = $"[Ingesting] {args.FilesProcessed} files... -> {Path.GetFileName(args.CurrentProcessingFile)}\n" + ConsoleOutput;
            });
        };
    }

    private void UpdatePropertiesFromState(PlaybackState state)
    {
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

    [RelayCommand]
    private void Play() => _queueService.Resume();

    [RelayCommand]
    private void Pause() => _queueService.Pause();

    [RelayCommand]
    private void SeekPlayback(double targetedSeconds)
    {
        double clamped = Math.Clamp(targetedSeconds, 0, DurationSeconds);
        _audioPlayer.Seek(clamped);
        PositionSeconds = clamped;
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

                foreach (var track in tracks)
                {
                    _queueService.Enqueue(track);
                }

                ConsoleOutput = $"[Harness] Pushed {tracks.Count} tracks to play queue. Activating track at index 0...\n" + ConsoleOutput;
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
}
