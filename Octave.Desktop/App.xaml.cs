using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Octave.Core.Services.Audio;
using Octave.Core.Services.Database;
using Octave.Core.Services.Library;
using Octave.Core.Interfaces;
using Octave.Core.Services.Playback;
using Octave.Core.Services.Metadata;
using Octave.Core.Models;
using Octave.Core.Helpers;
using Octave_Desktop.ViewModels;
using Octave_Desktop.Services.System;
using System.IO;

namespace Octave_Desktop;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public static IServiceProvider Services { get; private set; } = null!;

    // Native handle and instance of the main window, exposed for WinRT pickers and DPI scaling queries.
    public static IntPtr MainWindowHandle { get; private set; }
    public static Window? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();

        UnhandledException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[App UnhandledException] {e.Message} (Handled={e.Handled})");
            Helpers.CrashLog.Write("App UnhandledException", e.Exception);

            // SH-10: only benign cancellations are swallowed. The old blanket
            // e.Handled = true turned every failure - including fatal DB/engine
            // corruption during startup - into silent wrong behavior: the
            // process kept running in an undefined state with nothing surfaced.
            // Everything else stays unhandled so the failure is loud and
            // diagnosable instead of a zombie session.
            if (e.Exception is OperationCanceledException)
            {
                e.Handled = true;
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[AppDomain UnhandledException] {e.ExceptionObject}");
            Helpers.CrashLog.Write($"AppDomain UnhandledException (IsTerminating={e.IsTerminating})", e.ExceptionObject as Exception);
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[TaskScheduler UnobservedTaskException] {e.Exception}");
            Helpers.CrashLog.Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                // Resolve the unpackaged local data path
                string localFolderPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Octave");
                System.IO.Directory.CreateDirectory(localFolderPath);
                string dbPath = System.IO.Path.Combine(localFolderPath, "octave.db");
                string cachePath = System.IO.Path.Combine(localFolderPath, "ArtworkCache");
                System.IO.Directory.CreateDirectory(cachePath);
                
                // DB Context
                var dbContext = new SqliteDbContext($"Data Source={dbPath}");
                services.AddSingleton(dbContext);
                services.AddSingleton<ILyricsRepository>(dbContext);

                // Dispatcher Service
                var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                var dispatcherService = new Services.WinUIDispatcherService(dispatcherQueue);
                services.AddSingleton<IDispatcherService>(dispatcherService);
                QueueItem.SetUIDispatcher(action => dispatcherService.ExecuteOnUIThread(action));

                // HTTP Client (with responsive 15s timeout) & LRCLIB Client
                services.AddSingleton(sp => new System.Net.Http.HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(15)
                });
                services.AddSingleton<ILrclibClient, LrclibClient>();
                services.AddSingleton<ILyricsMatcher, LyricsMatcher>();

                // Artwork Cache Manager
                services.AddSingleton<IArtworkCacheManager>(new ArtworkCacheManager(cachePath));

                // Engine
                services.AddSingleton<IAudioPlayerService, ManagedBassAudioService>();

                // Harvester (Singleton is critical to share events broadcast instance)
                services.AddSingleton<ILibraryScanner, LocalLibraryScanner>();
                services.AddSingleton<ILibraryWatcherService, LibraryWatcherService>();

                // Facades & Services
                services.AddSingleton<ILibraryService, LibraryService>();
                services.AddSingleton<IQueueService, QueueService>();
                services.AddSingleton<IPlaylistService, PlaylistService>();
                services.AddSingleton<ISmtcService, WindowsSmtcService>();
                services.AddSingleton<ILyricsService, LyricsService>();

                // ViewModels
                services.AddSingleton<ShellViewModel>();
                services.AddTransient<HomeViewModel>();
                services.AddTransient<LibraryViewModel>();
                services.AddTransient<AlbumsViewModel>();
                services.AddTransient<ArtistsViewModel>();
                services.AddTransient<PlaylistsViewModel>();
                services.AddTransient<PlaylistDetailViewModel>();
                services.AddTransient<EntityDetailViewModel>();
                services.AddTransient<SearchViewModel>();
                services.AddSingleton<NowPlayingViewModel>();
            })
            .Build();

        Services = host.Services;

        // Startup Diagnostic Logging
        string processArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        string bassDllArch = "Unknown";
        string localFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Octave");
        string dbFile = System.IO.Path.Combine(localFolder, "octave.db");

        try
        {
            string bassDllPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "bass.dll");
            if (System.IO.File.Exists(bassDllPath))
            {
                using (var fs = new System.IO.FileStream(bassDllPath, System.IO.FileMode.Open, System.IO.FileAccess.Read))
                {
                    byte[] bytes = new byte[4];
                    fs.Seek(0x3c, System.IO.SeekOrigin.Begin);
                    int read = fs.Read(bytes, 0, 4);
                    if (read == 4)
                    {
                        uint peOffset = System.BitConverter.ToUInt32(bytes, 0);
                        fs.Seek(peOffset + 4, System.IO.SeekOrigin.Begin);
                        read = fs.Read(bytes, 0, 2);
                        if (read == 2)
                        {
                            ushort machine = System.BitConverter.ToUInt16(bytes, 0);
                            bassDllArch = machine == 0x014c ? "x86" : machine == 0x8664 ? "x64" : $"Unknown (0x{machine:X4})";
                        }
                    }
                }
            }
            else
            {
                bassDllArch = "File Missing";
            }
        }
        catch (Exception ex)
        {
            bassDllArch = $"Error reading: {ex.Message}";
        }

        System.Diagnostics.Debug.WriteLine($"[Startup Diagnostics] Process Architecture: {processArch}");
        System.Diagnostics.Debug.WriteLine($"[Startup Diagnostics] bass.dll Architecture: {bassDllArch}");
        System.Diagnostics.Debug.WriteLine($"[Startup Diagnostics] Database Path: {dbFile}");

        // Database Initialization before Window activation
        try
        {
            var dbContext = Services.GetRequiredService<SqliteDbContext>();
            await dbContext.InitializeAsync();
            System.Diagnostics.Debug.WriteLine("[Startup Diagnostics] Database Initialization: SUCCESS");

            _ = InitializeWatcherAsync();
        }
        catch (Exception ex)
        {
            // SYS-02: this throw sat inside async void OnLaunched, where the old
            // blanket UI handler swallowed it - the process survived as an
            // invisible zombie that held the DB file but showed no window. Fail
            // visibly (no XamlRoot exists yet, so a native message box is the
            // only reliable surface), then exit.
            System.Diagnostics.Debug.WriteLine($"[Startup Diagnostics] Database Initialization: FAILED - {ex}");
            ShowFatalStartupError(
                "OCTAVE could not initialize its local database and has to close.",
                $"{ex.GetType().Name}: {ex.Message}\n\nDatabase path: {dbFile}");
            Exit();
            return;
        }

        _window = new MainWindow();
        MainWindowInstance = _window;

        // Retrieve native window handle and initialize SMTC platform controller
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        MainWindowHandle = hwnd;
        var smtcService = Services.GetRequiredService<ISmtcService>();
        smtcService.Initialize(hwnd);

        _window.Activate();

        // Restore the previous session's queue AFTER the window is shown, so the
        // resume work never delays the initial UI boot (Milestone 4 gate).
        // INT-05: observe faults instead of letting a transient DB hiccup vanish into
        // an unobserved task as a silent no-restore.
        var queueService = Services.GetRequiredService<IQueueService>();
        try
        {
            await queueService.RestoreAsync();
            await HandleCommandLinePlaybackAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Startup Diagnostics] Queue restore / CLI launch failed: {ex.Message}");
        }
    }

    private static async Task HandleCommandLinePlaybackAsync()
    {
        try
        {
            string[] cmdArgs = Environment.GetCommandLineArgs();
            if (cmdArgs == null || cmdArgs.Length <= 1) return;

            string? targetFilePath = null;
            for (int i = 1; i < cmdArgs.Length; i++)
            {
                string arg = cmdArgs[i].Trim('"');
                if (System.IO.File.Exists(arg) && AudioFormatRegistry.IsSupported(System.IO.Path.GetExtension(arg)))
                {
                    targetFilePath = arg;
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(targetFilePath))
            {
                var scanner = Services.GetRequiredService<ILibraryScanner>();
                var db = Services.GetRequiredService<SqliteDbContext>();
                var queue = Services.GetRequiredService<IQueueService>();

                string trackId = IdGenerator.FromTrackUri(targetFilePath);
                var existingTrack = await db.GetTrackByIdAsync(trackId);
                if (existingTrack == null)
                {
                    await scanner.ScanFileAsync(targetFilePath);
                    existingTrack = await db.GetTrackByIdAsync(trackId);
                }

                if (existingTrack != null)
                {
                    queue.Clear();
                    queue.Enqueue(existingTrack);
                    queue.PlayIndex(0);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Startup Diagnostics] HandleCommandLinePlaybackAsync failed: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_ICONERROR = 0x00000010;

    // SYS-02: called before any Window exists - there is no XamlRoot for a
    // ContentDialog, so a native message box is the only reliable surface.
    private static void ShowFatalStartupError(string title, string details)
    {
        try
        {
            _ = MessageBoxW(IntPtr.Zero, details, $"OCTAVE - {title}", MB_ICONERROR);
        }
        catch
        {
            // Nothing left to do if even the box fails; Exit() still runs.
        }
    }

    private static async Task InitializeWatcherAsync()
    {
        try
        {
            var db = Services.GetRequiredService<SqliteDbContext>();
            var watcher = Services.GetRequiredService<ILibraryWatcherService>();
            var folders = await db.GetMonitoredFoldersAsync();

            // Self-healing: if MonitoredFolders is empty but tracks exist in the database,
            // automatically infer and register the root folder(s) so real-time watching
            // and startup reconciliation become active immediately.
            if (folders.Count == 0)
            {
                var inferred = await db.InferMonitoredFoldersFromTracksAsync();
                foreach (var inf in inferred)
                {
                    await db.AddMonitoredFolderAsync(inf);
                    folders.Add(inf);
                    System.Diagnostics.Debug.WriteLine($"[Startup Diagnostics] Inferred monitored folder from existing tracks: {inf}");
                }
            }

            if (folders.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[Startup Diagnostics] No monitored folders configured; library watching idle until the user adds one.");
            }
            foreach (var folder in folders)
            {
                watcher.AddMonitoredPath(folder);
            }

            // Sync the registered folders with ShellViewModel so Settings UI displays them
            try
            {
                var shellVm = Services.GetService<ViewModels.ShellViewModel>();
                if (shellVm != null)
                {
                    _ = shellVm.LoadFoldersAsync();
                }
            }
            catch { }

            System.Diagnostics.Debug.WriteLine("[Startup Diagnostics] Library Watcher Service: INITIALIZED");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Startup Diagnostics] Library Watcher Service initialization failed: {ex.Message}");
        }
    }
}
