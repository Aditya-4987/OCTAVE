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

    public App()
    {
        InitializeComponent();
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

                // Watcher Service (registers after scanner is available)
                services.AddSingleton<ILibraryWatcherService>(provider =>
                {
                    var scanner = provider.GetRequiredService<ILibraryScanner>();
                    var monitoredPaths = new[] { System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyMusic) };
                    return new LibraryWatcherService(scanner, monitoredPaths);
                });

                // Facades
                services.AddSingleton<ILibraryService, LibraryService>();
                services.AddSingleton<IQueueService, QueueService>();
                services.AddSingleton<ISmtcService, WindowsSmtcService>();

                // ViewModels
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<ShellViewModel>();
                services.AddTransient<LibraryViewModel>();
                services.AddTransient<AlbumsViewModel>();
                services.AddTransient<ArtistsViewModel>();
                services.AddTransient<EntityDetailViewModel>();
                services.AddTransient<SearchViewModel>();
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

            // Initialize Library Watcher Service strictly after database migration completes
            var watcherService = Services.GetRequiredService<ILibraryWatcherService>();
            System.Diagnostics.Debug.WriteLine("[Startup Diagnostics] Library Watcher Service: INITIALIZED");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Startup Diagnostics] Database Initialization: FAILED - {ex}");
            throw;
        }

        _window = new MainWindow();

        // Retrieve native window handle and initialize SMTC platform controller
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var smtcService = Services.GetRequiredService<ISmtcService>();
        smtcService.Initialize(hwnd);

        _window.Activate();
    }
}
