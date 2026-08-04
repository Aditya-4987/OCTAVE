using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Octave_Desktop.Views;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; }

    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
        this.Loaded += HomePage_Loaded;
        this.Unloaded += HomePage_Unloaded;
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Cleanup();
    }

    public Visibility SectionVisibility(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AllEmptyVisibility(int a, int b, int c, int d) =>
        (a + b + c + d) == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void HeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.RecentlyPlayed.Count > 0)
        {
            ViewModel.PlaySection(ViewModel.RecentlyPlayed, ViewModel.RecentlyPlayed[0]);
        }
        else if (ViewModel.LastAdded.Count > 0)
        {
            ViewModel.PlaySection(ViewModel.LastAdded, ViewModel.LastAdded[0]);
        }
    }

    private void QuickPlay_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackDisplayItem item) ViewModel.PlaySection(ViewModel.QuickPlayItems, item);
    }

    private void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        this.Frame?.Navigate(typeof(SettingsPage));
    }

    private void Recently_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackDisplayItem item) ViewModel.PlaySection(ViewModel.RecentlyPlayed, item);
    }

    private void Most_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackDisplayItem item) ViewModel.PlaySection(ViewModel.MostPlayed, item);
    }

    private void LastAdded_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackDisplayItem item) ViewModel.PlaySection(ViewModel.LastAdded, item);
    }

    private void Favorites_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackDisplayItem item) ViewModel.PlaySection(ViewModel.Favorites, item);
    }

    private async void TrackCard_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        Track? track = fe.DataContext switch
        {
            TrackDisplayItem item => item.Track,
            Track t => t,
            _ => null
        };

        if (track == null) return;

        var libraryService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Octave.Core.Services.Library.ILibraryService>(App.Services);
        var playlistService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Octave.Core.Interfaces.IPlaylistService>(App.Services);
        var queueService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Octave.Core.Interfaces.IQueueService>(App.Services);

        await Helpers.TrackContextMenu.ShowAsync(fe, track, libraryService, playlistService, queueService, this.XamlRoot);
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            if (Application.Current.Resources.TryGetValue("SystemControlHighlightAccentBrush", out var accentObj) && accentObj is SolidColorBrush accent)
            {
                grid.BorderBrush = accent;
            }
        }
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            grid.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent) { Color = Microsoft.UI.ColorHelper.FromArgb(255, 42, 42, 42) }; // #2A2A2A
        }
    }
}
