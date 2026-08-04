using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;

namespace Octave_Desktop.Views;

public sealed partial class PlaylistsPage : Page
{
    public PlaylistsViewModel ViewModel { get; }

    public PlaylistsPage()
    {
        ViewModel = App.Services.GetRequiredService<PlaylistsViewModel>();
        InitializeComponent();
        this.Loaded += PlaylistsPage_Loaded;
        this.Unloaded += PlaylistsPage_Unloaded;
    }

    private async void PlaylistsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void PlaylistsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Cleanup();
    }

    public Visibility EmptyVisibility(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void GridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Playlist playlist)
        {
            Frame.Navigate(typeof(PlaylistDetailPage), playlist.Id);
        }
    }

    private async void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (this.XamlRoot is null) return;

            var input = new TextBox { PlaceholderText = "Playlist name" };
            var dialog = new ContentDialog
            {
                Title = "New Playlist",
                Content = input,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.CreateAsync(input.Text);
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Playlists] New playlist dialog failed: {ex}");
        }
    }

    private void PlaylistCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Playlist playlist)
        {
            var flyout = new MenuFlyout();
            var delete = new MenuFlyoutItem { Text = "Delete playlist" };
            delete.Click += (s, a) => ViewModel.DeletePlaylistCommand.Execute(playlist);
            flyout.Items.Add(delete);
            flyout.ShowAt(fe, e.GetPosition(fe));
        }
    }
}
