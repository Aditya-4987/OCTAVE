using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;
using Octave.Core.Interfaces;
using System;
using System.Runtime.InteropServices;

namespace Octave_Desktop.Views;

public sealed partial class PlaylistDetailPage : Page
{
    public PlaylistDetailViewModel ViewModel { get; }

    private readonly System.Collections.Generic.List<Button> _playButtons = new();
    private readonly System.Collections.Generic.List<Button> _favoriteButtons = new();
    private readonly System.Collections.Generic.HashSet<string> _favoriteTrackIds = new(StringComparer.Ordinal);
    private readonly Octave.Core.Services.Library.ILibraryService _libraryService;
    private readonly EventHandler _favoritesChangedHandler;

    public PlaylistDetailPage()
    {
        ViewModel = App.Services.GetRequiredService<PlaylistDetailViewModel>();
        _libraryService = App.Services.GetRequiredService<Octave.Core.Services.Library.ILibraryService>();
        InitializeComponent();
        this.Unloaded += PlaylistDetailPage_Unloaded;

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        _favoritesChangedHandler = (s, e) =>
        {
            _ = RefreshFavoriteIdsAsync();
        };
        _libraryService.FavoritesChanged += _favoritesChangedHandler;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = RefreshFavoriteIdsAsync();
        if (e.Parameter is string playlistId)
        {
            await ViewModel.LoadAsync(playlistId);
            _ = RefreshFavoriteIdsAsync();
        }
    }

    private void PlaylistDetailPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _libraryService.FavoritesChanged -= _favoritesChangedHandler;
        ViewModel.Cleanup();
    }

    private async Task RefreshFavoriteIdsAsync()
    {
        try
        {
            var favIds = await _libraryService.GetFavoriteTrackIdsAsync();
            DispatcherQueue.TryEnqueue(() =>
            {
                _favoriteTrackIds.Clear();
                foreach (var id in favIds) _favoriteTrackIds.Add(id);
                _favoriteButtons.RemoveAll(btn => btn.XamlRoot == null);
                foreach (var btn in _favoriteButtons)
                {
                    UpdateFavoriteButtonIcon(btn);
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlaylistDetail] RefreshFavoriteIdsAsync failed: {ex.Message}");
        }
    }

    private void FavoriteButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && !_favoriteButtons.Contains(btn))
        {
            _favoriteButtons.Add(btn);
            UpdateFavoriteButtonIcon(btn);
        }
    }

    private void FavoriteButton_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Button btn)
        {
            UpdateFavoriteButtonIcon(btn);
        }
    }

    private void UpdateFavoriteButtonIcon(Button btn)
    {
        if (btn.Content is FontIcon fontIcon && btn.DataContext is Track track)
        {
            bool isFav = _favoriteTrackIds.Contains(track.Id);
            fontIcon.Glyph = isFav ? "\uEB52" : "\uEB51";
            fontIcon.Foreground = isFav
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 64, 96))
                : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
            ToolTipService.SetToolTip(btn, isFav ? "Remove from Favorites" : "Add to Favorites");
        }
    }

    private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is Track track)
        {
            bool newFav = await _libraryService.ToggleFavoriteAsync(track.Id);
            if (newFav) _favoriteTrackIds.Add(track.Id);
            else _favoriteTrackIds.Remove(track.Id);
            UpdateFavoriteButtonIcon(btn);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistDetailViewModel.CurrentPlayingTrackId) ||
            e.PropertyName == nameof(PlaylistDetailViewModel.IsCurrentlyPlaying))
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

    private void ListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track)
        {
            ViewModel.PlayTrackCommand.Execute(track);
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
        if (btn.Content is FontIcon fontIcon && btn.DataContext is Track track)
        {
            bool isCurrent = track.Id == ViewModel.CurrentPlayingTrackId;
            bool isPlaying = ViewModel.IsCurrentlyPlaying;
            fontIcon.Glyph = (isCurrent && isPlaying) ? "" : "";
        }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is Track track)
        {
            if (track.Id == ViewModel.CurrentPlayingTrackId)
            {
                if (ViewModel.IsCurrentlyPlaying) ViewModel.PausePlayback();
                else ViewModel.ResumePlayback();
            }
            else
            {
                ViewModel.PlayTrackCommand.Execute(track);
            }
        }
    }

    private async void TrackRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not Track track) return;

        var libraryService = App.Services.GetRequiredService<Octave.Core.Services.Library.ILibraryService>();
        var playlistService = App.Services.GetRequiredService<IPlaylistService>();
        var queueService = App.Services.GetRequiredService<IQueueService>();

        await Helpers.TrackContextMenu.ShowAsync(
            fe, track, libraryService, playlistService, queueService, this.XamlRoot,
            onRemoveFromPlaylist: (t) => ViewModel.RemoveTrackCommand.Execute(t)
        );
    }

    private async void MoreMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not Track track) return;

        var libraryService = App.Services.GetRequiredService<Octave.Core.Services.Library.ILibraryService>();
        var playlistService = App.Services.GetRequiredService<IPlaylistService>();
        var queueService = App.Services.GetRequiredService<IQueueService>();

        await Helpers.TrackContextMenu.ShowAsync(
            fe, track, libraryService, playlistService, queueService, this.XamlRoot,
            onRemoveFromPlaylist: (t) => ViewModel.RemoveTrackCommand.Execute(t)
        );
    }

    // UI-PL-01: deleting a playlist used to happen on the very first click with
    // no way back. Ask for confirmation first, matching the app's other
    // destructive-action dialogs.
    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (this.XamlRoot is null) return;

            var dialog = new ContentDialog
            {
                Title = "Delete playlist?",
                Content = $"\"{ViewModel.Title}\" will be permanently deleted. The tracks themselves stay in your library.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            ViewModel.DeleteSelfCommand.Execute(null);
            if (Frame.CanGoBack) Frame.GoBack();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlaylistDetail] Delete dialog failed: {ex}");
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    public Visibility SelectionBarVisibility(bool isSelectionMode) => isSelectionMode ? Visibility.Visible : Visibility.Collapsed;
    public bool IsClickEnabled(bool isSelectionMode) => !isSelectionMode;
    public bool CanReorder(bool isSelectionMode) => !isSelectionMode;
    public ListViewSelectionMode ResolveSelectionMode(bool isSelectionMode) => isSelectionMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
    public Thickness TrackListMargin(bool isSelectionMode) => isSelectionMode ? new Thickness(0, 52, 0, 0) : new Thickness(0);

    private void ClearSelectionSafely()
    {
        if (TrackList.SelectionMode == ListViewSelectionMode.None)
        {
            return;
        }

        try
        {
            TrackList.SelectedItems.Clear();
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x8000FFFF)
        {
        }
    }

    private void SelectAllSafely()
    {
        if (TrackList.SelectionMode == ListViewSelectionMode.None)
        {
            return;
        }

        try
        {
            TrackList.SelectAll();
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x8000FFFF)
        {
        }
    }

    private void ToggleSelectMode_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsSelectionMode)
        {
            ClearSelectionSafely();
            ViewModel.IsSelectionMode = false;
        }
        else
        {
            ViewModel.IsSelectionMode = true;
        }
    }

    private void ExitSelectMode_Click(object sender, RoutedEventArgs e)
    {
        ClearSelectionSafely();
        ViewModel.IsSelectionMode = false;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (TrackList.SelectionMode == ListViewSelectionMode.None) return;
        if (TrackList.SelectedItems.Count >= ViewModel.Tracks.Count && ViewModel.Tracks.Count > 0)
        {
            ClearSelectionSafely();
        }
        else
        {
            SelectAllSafely();
        }
    }

    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int count = TrackList.SelectedItems.Count;
        if (SelectedCountTextBlock != null)
        {
            SelectedCountTextBlock.Text = count == 1 ? "1 song selected" : $"{count} songs selected";
        }
        if (PlaySelectedBtn != null) PlaySelectedBtn.IsEnabled = count > 0;
        if (QueueSelectedBtn != null) QueueSelectedBtn.IsEnabled = count > 0;
        if (RemoveSelectedBtn != null) RemoveSelectedBtn.IsEnabled = count > 0;
    }

    private void PlaySelected_Click(object sender, RoutedEventArgs e)
    {
        var tracks = TrackList.SelectedItems.OfType<Track>().ToList();
        if (tracks.Count > 0)
        {
            ViewModel.PlaySelectedTracks(tracks);
            ClearSelectionSafely();
            ViewModel.IsSelectionMode = false;
        }
    }

    private void AddSelectedToQueue_Click(object sender, RoutedEventArgs e)
    {
        var tracks = TrackList.SelectedItems.OfType<Track>().ToList();
        if (tracks.Count > 0)
        {
            ViewModel.AddSelectedTracksToQueue(tracks);
            ClearSelectionSafely();
            ViewModel.IsSelectionMode = false;
        }
    }

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var tracks = TrackList.SelectedItems.OfType<Track>().ToList();
        if (tracks.Count > 0)
        {
            ClearSelectionSafely();
            ViewModel.IsSelectionMode = false;
            await ViewModel.RemoveSelectedTracksAsync(tracks);
        }
    }

    private async void AddSongs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (this.XamlRoot is null) return;

            var allTracks = await ViewModel.GetAllLibraryTracksAsync();
            if (allTracks.Count == 0)
            {
                var emptyDialog = new ContentDialog
                {
                    Title = "No songs in library",
                    Content = "Your music library is currently empty. Add songs to your library folder first.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await emptyDialog.ShowAsync();
                return;
            }

            // Exclude tracks that are already in the playlist or allow them as duplicates
            var existingTrackIds = new System.Collections.Generic.HashSet<string>(ViewModel.Tracks.Select(t => t.Id));

            var container = new Grid { Width = 480, Height = 420 };
            container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var searchBox = new TextBox
            {
                PlaceholderText = "Search library songs by title or artist...",
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(searchBox, 0);

            var songList = new ListView
            {
                SelectionMode = ListViewSelectionMode.Multiple,
                ItemsSource = allTracks,
                ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(@"
                    <DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                        <Grid Padding=""8,6"" Height=""44"">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width=""*"" />
                                <ColumnDefinition Width=""Auto"" />
                            </Grid.ColumnDefinitions>
                            <StackPanel Grid.Column=""0"" VerticalAlignment=""Center"">
                                <TextBlock Text=""{Binding Title}"" FontWeight=""SemiBold"" FontSize=""13"" TextTrimming=""CharacterEllipsis"" />
                                <TextBlock Text=""{Binding ArtistName}"" FontSize=""11"" Foreground=""{ThemeResource TextFillColorSecondaryBrush}"" TextTrimming=""CharacterEllipsis"" />
                            </StackPanel>
                            <TextBlock Grid.Column=""1"" Text=""{Binding AlbumTitle}"" FontSize=""11"" Foreground=""{ThemeResource TextFillColorTertiaryBrush}"" VerticalAlignment=""Center"" Margin=""8,0,0,0"" MaxWidth=""120"" TextTrimming=""CharacterEllipsis"" />
                        </Grid>
                    </DataTemplate>")
            };
            Grid.SetRow(songList, 1);

            searchBox.TextChanged += (s, args) =>
            {
                string query = searchBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(query))
                {
                    songList.ItemsSource = allTracks;
                }
                else
                {
                    songList.ItemsSource = allTracks.Where(t =>
                        (t.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (t.ArtistName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (t.AlbumTitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                    ).ToList();
                }
            };

            container.Children.Add(searchBox);
            container.Children.Add(songList);

            var dialog = new ContentDialog
            {
                Title = $"Add Songs to {ViewModel.Title}",
                Content = container,
                PrimaryButtonText = "Add Selected",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var selected = songList.SelectedItems.OfType<Track>().ToList();
                if (selected.Count > 0)
                {
                    await ViewModel.AddTracksToPlaylistAsync(selected);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlaylistDetail] Add songs dialog error: {ex}");
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
