using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;
using Microsoft.UI.Xaml;

namespace Octave_Desktop.Views;

public sealed partial class GenresPage : Page
{
    public GenresViewModel ViewModel { get; }

    public GenresPage()
    {
        ViewModel = App.Services.GetRequiredService<GenresViewModel>();
        InitializeComponent();
        this.Loaded += GenresPage_Loaded;
        this.Unloaded += GenresPage_Unloaded;
    }

    private void GenresPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Cleanup();
    }

    private async void GenresPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void GridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string genre)
        {
            Frame.Navigate(typeof(EntityDetailPage), new EntityNavigationParameter(EntityType.Genre, genre));
        }
    }
}
