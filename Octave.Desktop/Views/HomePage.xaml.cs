using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;

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

    private void Recently_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track) ViewModel.PlaySection(ViewModel.RecentlyPlayed, track);
    }

    private void Most_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track) ViewModel.PlaySection(ViewModel.MostPlayed, track);
    }

    private void LastAdded_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track) ViewModel.PlaySection(ViewModel.LastAdded, track);
    }

    private void Favorites_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track) ViewModel.PlaySection(ViewModel.Favorites, track);
    }
}
