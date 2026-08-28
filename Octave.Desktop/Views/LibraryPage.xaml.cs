using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Microsoft.UI.Xaml;
using System;
using System.Threading.Tasks;

namespace Octave_Desktop.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryViewModel ViewModel { get; }
    private readonly IPlaylistService _playlistService;
    private readonly Octave.Core.Services.Library.ILibraryService _libraryService;

    private readonly System.Collections.Generic.List<Button> _playButtons = new();

    public LibraryPage()
    {
        ViewModel = App.Services.GetRequiredService<LibraryViewModel>();
        _playlistService = App.Services.GetRequiredService<IPlaylistService>();
        _libraryService = App.Services.GetRequiredService<Octave.Core.Services.Library.ILibraryService>();
        InitializeComponent();
        this.Loaded += LibraryPage_Loaded;
        this.Unloaded += LibraryPage_Unloaded;

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private async void TrackRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not LibraryTrackItem item) return;

        var queueService = App.Services.GetRequiredService<IQueueService>();
        await Helpers.TrackContextMenu.ShowAsync(fe, item.Track, _libraryService, _playlistService, queueService, this.XamlRoot);
    }

    private async void MoreMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not LibraryTrackItem item) return;

        var queueService = App.Services.GetRequiredService<IQueueService>();
        await Helpers.TrackContextMenu.ShowAsync(fe, item.Track, _libraryService, _playlistService, queueService, this.XamlRoot);
    }

    // Old CreatePlaylistWithTrackAsync moved to helper

    private void LibraryPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Cleanup();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.CurrentPlayingTrackId) ||
            e.PropertyName == nameof(LibraryViewModel.IsCurrentlyPlaying))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _playButtons.RemoveAll(btn => btn.XamlRoot == null);
                foreach (var btn in _playButtons)
                {
                    UpdatePlayButtonIcon(btn);
                }
            });
        }
    }

    private async void LibraryPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (Resources["SkeletonPulse"] is Microsoft.UI.Xaml.Media.Animation.Storyboard pulse)
        {
            pulse.Begin();
        }
        await ViewModel.LoadAsync();
    }

    public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyVisibility(int itemCount, bool isLoading) =>
        (!isLoading && itemCount == 0) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NormalHeaderVisibility(bool isSelectionMode) =>
        isSelectionMode ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SelectionHeaderVisibility(bool isSelectionMode) =>
        isSelectionMode ? Visibility.Visible : Visibility.Collapsed;

    public ListViewSelectionMode ResolveSelectionMode(bool isSelectionMode) =>
        isSelectionMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;

    public bool IsClickEnabled(bool isSelectionMode) => !isSelectionMode;

    private void ToggleSelectionMode_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsSelectionMode)
        {
            try { TracksListView.SelectedItems?.Clear(); } catch { }
            ViewModel.IsSelectionMode = false;
        }
        else
        {
            ViewModel.IsSelectionMode = true;
        }
    }

    private void ExitSelectionMode_Click(object sender, RoutedEventArgs e)
    {
        try { TracksListView.SelectedItems?.Clear(); } catch { }
        ViewModel.IsSelectionMode = false;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (TracksListView.SelectionMode == ListViewSelectionMode.None) return;
        if (TracksListView.SelectedItems.Count >= ViewModel.Items.Count && ViewModel.Items.Count > 0)
        {
            try { TracksListView.SelectedItems.Clear(); } catch { }
        }
        else
        {
            try { TracksListView.SelectAll(); } catch { }
        }
    }

    private void TracksListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int count = TracksListView.SelectedItems.Count;
        if (SelectedCountTextBlock != null)
        {
            SelectedCountTextBlock.Text = count == 1 ? "1 song selected" : $"{count} songs selected";
        }
        if (PlaySelectedBtn != null) PlaySelectedBtn.IsEnabled = count > 0;
        if (QueueSelectedBtn != null) QueueSelectedBtn.IsEnabled = count > 0;
        if (PlaylistSelectedBtn != null) PlaylistSelectedBtn.IsEnabled = count > 0;
    }

    private void PlaySelected_Click(object sender, RoutedEventArgs e)
    {
        var tracks = TracksListView.SelectedItems.OfType<LibraryTrackItem>().Select(i => i.Track).ToList();
        if (tracks.Count > 0)
        {
            ViewModel.PlaySelectedTracks(tracks);
            try { TracksListView.SelectedItems?.Clear(); } catch { }
            ViewModel.IsSelectionMode = false;
        }
    }

    private void AddSelectedToQueue_Click(object sender, RoutedEventArgs e)
    {
        var tracks = TracksListView.SelectedItems.OfType<LibraryTrackItem>().Select(i => i.Track).ToList();
        if (tracks.Count > 0)
        {
            ViewModel.AddSelectedTracksToQueue(tracks);
            try { TracksListView.SelectedItems?.Clear(); } catch { }
            ViewModel.IsSelectionMode = false;
        }
    }

    private async void AddSelectedToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var tracks = TracksListView.SelectedItems.OfType<LibraryTrackItem>().Select(i => i.Track).ToList();
        if (tracks.Count == 0) return;

        if (sender is FrameworkElement fe)
        {
            var playlists = await ViewModel.GetPlaylistsAsync();
            var flyout = new MenuFlyout();

            var newPlaylistItem = new MenuFlyoutItem { Text = "+ New Playlist..." };
            newPlaylistItem.Click += async (s, a) =>
            {
                await PromptCreatePlaylistWithTracksAsync(tracks);
            };
            flyout.Items.Add(newPlaylistItem);

            if (playlists.Count > 0)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                foreach (var pl in playlists)
                {
                    var item = new MenuFlyoutItem { Text = pl.Title };
                    item.Click += async (s, a) =>
                    {
                        try { TracksListView.SelectedItems?.Clear(); } catch { }
                        ViewModel.IsSelectionMode = false;
                        await ViewModel.AddSelectedTracksToPlaylistAsync(pl.Id, tracks);
                    };
                    flyout.Items.Add(item);
                }
            }

            flyout.ShowAt(fe);
        }
    }

    private async Task PromptCreatePlaylistWithTracksAsync(System.Collections.Generic.List<Track> tracks)
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
                IsPrimaryButtonEnabled = false,
                XamlRoot = this.XamlRoot
            };
            input.TextChanged += (s, args) =>
                dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
            {
                await ViewModel.CreatePlaylistWithTracksAsync(input.Text, tracks);
                ViewModel.IsSelectionMode = false;
                TracksListView.SelectedItems.Clear();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Library] Create playlist with tracks failed: {ex}");
        }
    }

    private void ListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LibraryTrackItem item)
        {
            ViewModel.PlayTrackCommand.Execute(item.Track);
        }
    }

    private void PlayButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && !_playButtons.Contains(btn))
        {
            _playButtons.Add(btn);
            UpdatePlayButtonIcon(btn);
        }
    }

    private void PlayButton_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Button btn)
        {
            UpdatePlayButtonIcon(btn);
        }
    }

    private void UpdatePlayButtonIcon(Button btn)
    {
        if (btn.Content is FontIcon fontIcon && btn.DataContext is LibraryTrackItem item)
        {
            bool isCurrent = item.Track.Id == ViewModel.CurrentPlayingTrackId;
            bool isPlaying = ViewModel.IsCurrentlyPlaying;
            fontIcon.Glyph = (isCurrent && isPlaying) ? "\uE769" : "\uE768";
        }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is LibraryTrackItem item)
        {
            if (item.Track.Id == ViewModel.CurrentPlayingTrackId)
            {
                if (ViewModel.IsCurrentlyPlaying)
                {
                    ViewModel.PausePlayback();
                }
                else
                {
                    ViewModel.ResumePlayback();
                }
            }
            else
            {
                ViewModel.PlayTrackCommand.Execute(item.Track);
            }
        }
    }

    private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is LibraryTrackItem item)
        {
            bool newFav = await ViewModel.ToggleFavoriteAsync(item.Track.Id);
            item.IsFavorite = newFav;
        }
    }

    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "0:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}
