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
using Octave_Desktop.ViewModels;
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

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                // Resolve the unpacked MSIX path
                string dbPath = System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "octave.db");
                
                // DB Context
                services.AddSingleton(new SqliteDbContext($"Data Source={dbPath}"));

                // Engine
                services.AddSingleton<IAudioPlayerService, ManagedBassAudioService>();

                // Harvester (Singleton is critical to share events broadcast instance)
                services.AddSingleton<LocalLibraryScanner>();

                // Facades
                services.AddSingleton<ILibraryService, LibraryService>();
                services.AddSingleton<IQueueService, QueueService>();

                // ViewModels
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<ShellViewModel>();
                services.AddTransient<LibraryViewModel>();
                services.AddTransient<AlbumsViewModel>();
                services.AddTransient<ArtistsViewModel>();
                services.AddTransient<EntityDetailViewModel>();
            })
            .Build();

        Services = host.Services;

        _window = new MainWindow();
        _window.Activate();
    }
}
