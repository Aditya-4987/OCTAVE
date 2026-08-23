using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Microsoft.UI.Xaml;

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

    // UI-AR-01: drives the empty-state panel. The Card_PointerEntered/Exited
    // brush-injection handlers are gone - card hover feedback is now a
    // PointerOver visual state inside the item template.
    public Visibility EmptyStateVisibility(int count) => count > 0 ? Visibility.Collapsed : Visibility.Visible;
}
