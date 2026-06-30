using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Microsoft.UI.Xaml;

namespace Octave_Desktop.Views;

// TODO:
// Album artwork extraction, caching and virtualization
// will be implemented in a future ticket.

public sealed partial class AlbumsPage : Page
{
    public AlbumsViewModel ViewModel { get; }

    public AlbumsPage()
    {
        ViewModel = App.Services.GetRequiredService<AlbumsViewModel>();
        InitializeComponent();
        this.Loaded += AlbumsPage_Loaded;
        this.Unloaded += AlbumsPage_Unloaded;
    }

    private void AlbumsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Cleanup();
    }

    private async void AlbumsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void GridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Octave.Core.Models.Album clickedAlbum)
        {
            Frame.Navigate(typeof(EntityDetailPage), new Octave.Core.Models.EntityNavigationParameter(Octave.Core.Models.EntityType.Album, clickedAlbum.Id));
        }
    }
}
