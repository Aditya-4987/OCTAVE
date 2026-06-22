using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Octave.Core.Services.Database;
using Octave.Core.Services.Library;
using Octave.Core.Interfaces;
using System;
using System.IO;
using System.Threading;

namespace Octave_Desktop;

public sealed partial class MainWindow : Window
{
    private bool _isHooked = false;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnStaticFireClicked(object sender, RoutedEventArgs e)
    {
        StaticFireButton.IsEnabled = false;
        ConsoleLog.Text = "[Harness] Static fire sequence ignited...\n" + ConsoleLog.Text;

        try
        {
            // Zero-IO Startup: DDL initialized on click rather than app launch
            ConsoleLog.Text = "[Initializing SQLite DDL Vault...]\n" + ConsoleLog.Text;
            var db = App.Services.GetRequiredService<SqliteDbContext>();
            await db.InitializeAsync();
            ConsoleLog.Text = "[Database initialized successfully]\n" + ConsoleLog.Text;

            var libraryService = App.Services.GetRequiredService<ILibraryService>();
            var queueService = App.Services.GetRequiredService<IQueueService>();
            var scanner = App.Services.GetRequiredService<LocalLibraryScanner>();

            if (!_isHooked)
            {
                // CRITICAL THREAD MARSHALING: Attach to scanner.ScanProgressChanged
                scanner.ScanProgressChanged += (s, args) =>
                {
                    DispatcherQueue.TryEnqueue(() => {
                        ConsoleLog.Text = $"[Ingesting] {args.FilesProcessed} files... -> {System.IO.Path.GetFileName(args.CurrentProcessingFile)}\n" + ConsoleLog.Text;
                    });
                };

                // Hook queueService.PlaybackStateChanged to print track transitions to ConsoleLog via DispatcherQueue
                queueService.PlaybackStateChanged += (s, state) =>
                {
                    DispatcherQueue.TryEnqueue(() => {
                        if (state.CurrentTrack != null)
                        {
                            ConsoleLog.Text = $"[Playback] {state.Status}: {state.CurrentTrack.Title} - {state.CurrentTrack.ArtistName} (Position: {state.PositionSeconds:F1}s / Duration: {state.DurationSeconds:F1}s)\n" + ConsoleLog.Text;
                        }
                        else
                        {
                            ConsoleLog.Text = $"[Playback] {state.Status}: No Track Loaded\n" + ConsoleLog.Text;
                        }
                    });
                };

                _isHooked = true;
            }

            // Resolve Windows Music folder
            string musicPath = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            ConsoleLog.Text = $"[Harness] Traversing special music folder: {musicPath}\n" + ConsoleLog.Text;

            // Await scan
            await libraryService.ScanLocalLibraryAsync(musicPath, CancellationToken.None);

            // Query count
            int count = await libraryService.GetTotalTrackCountAsync();
            ConsoleLog.Text = $"[Harness] Scanning finished. Total database tracks: {count}\n" + ConsoleLog.Text;

            if (count > 0)
            {
                var tracks = await libraryService.GetAllTracksAsync();
                queueService.Clear();

                foreach (var track in tracks)
                {
                    queueService.Enqueue(track);
                }

                ConsoleLog.Text = $"[Harness] Pushed {tracks.Count} tracks to play queue. Activating track at index 0...\n" + ConsoleLog.Text;
                queueService.PlayIndex(0);
            }
            else
            {
                ConsoleLog.Text = "[Harness] Warn: No local tracks found in special music folder to play.\n" + ConsoleLog.Text;
            }

            ConsoleLog.Text = "[Harness] Static fire launchpad sequence complete.\n" + ConsoleLog.Text;
        }
        catch (Exception ex)
        {
            ConsoleLog.Text = $"[Harness] FAILED: {ex.Message}\n" + ConsoleLog.Text;
        }
        finally
        {
            StaticFireButton.IsEnabled = true;
        }
    }
}