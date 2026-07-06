using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Octave_Desktop.Views;

// TODO:
// Album artwork extraction, caching and virtualization
// will be implemented in a future ticket.

public sealed partial class ArtistsPage : Page
{
    public ArtistsViewModel ViewModel { get; }

    public ArtistsPage()
    {
        ViewModel = App.Services.GetRequiredService<ArtistsViewModel>();
        InitializeComponent();
        this.Loaded += ArtistsPage_Loaded;
        this.Unloaded += ArtistsPage_Unloaded;
    }

    private void ArtistsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Cleanup();
    }

    private async void ArtistsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void GridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Octave.Core.Models.Artist clickedArtist)
        {
            Frame.Navigate(typeof(EntityDetailPage), new Octave.Core.Models.EntityNavigationParameter(Octave.Core.Models.EntityType.Artist, clickedArtist.Id));
        }
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
