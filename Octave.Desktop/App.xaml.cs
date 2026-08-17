using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
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
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[AppDomain UnhandledException] {e.ExceptionObject}");
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[TaskScheduler UnobservedTaskException] {e.Exception}");
            e.SetObserved();
        };
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                // Resolve the unpacked MSIX path
                string localFolderPath = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                string dbPath = System.IO.Path.Combine(localFolderPath, "octave.db");
                string cachePath = System.IO.Path.Combine(localFolderPath, "ArtworkCache");
                System.IO.Directory.CreateDirectory(cachePath);
                
                // DB Context
                services.AddSingleton(new SqliteDbContext($"Data Source={dbPath}"));

                // Artwork Cache Manager
                services.AddSingleton<IArtworkCacheManager>(new ArtworkCacheManager(cachePath));

                // Engine
                services.AddSingleton<IAudioPlayerService, ManagedBassAudioService>();

                // Harvester (Singleton is critical to share events broadcast instance)
                services.AddSingleton<ILibraryScanner, LocalLibraryScanner>();
                services.AddSingleton<ILibraryWatcherService, LibraryWatcherService>();

                // Facades
                services.AddSingleton<ILibraryService, LibraryService>();
                services.AddSingleton<IQueueService, QueueService>();
                services.AddSingleton<IPlaylistService, PlaylistService>();
                services.AddSingleton<ISmtcService, WindowsSmtcService>();
                services.AddSingleton<ILyricsService, Octave.Core.Services.Metadata.LyricsService>();

                // ViewModels
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<ShellViewModel>();
                services.AddTransient<HomeViewModel>();
                services.AddTransient<LibraryViewModel>();
                services.AddTransient<AlbumsViewModel>();
                services.AddTransient<ArtistsViewModel>();
                services.AddTransient<PlaylistsViewModel>();
                services.AddTransient<PlaylistDetailViewModel>();
                services.AddTransient<EntityDetailViewModel>();
                services.AddTransient<SearchViewModel>();
                services.AddTransient<NowPlayingViewModel>();
            })
            .Build();

        Services = host.Services;

        // Startup Diagnostic Logging
        string processArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        string bassDllArch = "Unknown";
        string localFolder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
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
            System.Diagnostics.Debug.WriteLine($"[Startup Diagnostics] Database Initialization: FAILED - {ex}");
            throw;
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
        var queueService = Services.GetRequiredService<IQueueService>();
        _ = queueService.RestoreAsync();
    }

    private static async Task InitializeWatcherAsync()
    {
        try
        {
            var db = Services.GetRequiredService<SqliteDbContext>();
            var watcher = Services.GetRequiredService<ILibraryWatcherService>();
            var folders = await db.GetMonitoredFoldersAsync();
            if (folders.Count == 0)
            {
                string myMusic = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyMusic);
                await db.AddMonitoredFolderAsync(myMusic);
                folders = new System.Collections.Generic.List<string> { myMusic };
            }
            foreach (var folder in folders)
            {
                watcher.AddMonitoredPath(folder);
            }
            System.Diagnostics.Debug.WriteLine("[Startup Diagnostics] Library Watcher Service: INITIALIZED");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Startup Diagnostics] Library Watcher Service initialization failed: {ex.Message}");
        }
    }
}
