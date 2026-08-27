using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Octave.Core.Services;
using Octave.Core.Services.Library;
using Octave.Core.Interfaces;
using System;

namespace Octave_Desktop.Views;

public sealed partial class SearchResultsPage : Page
{
    public SearchViewModel ViewModel { get; }
    private readonly System.Collections.Generic.List<Button> _playButtons = new();
    private readonly System.Collections.Generic.List<Button> _favoriteButtons = new();
    private readonly System.Collections.Generic.HashSet<string> _favoriteTrackIds = new(StringComparer.Ordinal);
    private readonly ILibraryService _libraryService;
    private readonly EventHandler _favoritesChangedHandler;

    public SearchResultsPage()
    {
        ViewModel = App.Services.GetRequiredService<SearchViewModel>();
        _libraryService = App.Services.GetRequiredService<ILibraryService>();
        InitializeComponent();
        this.Unloaded += SearchResultsPage_Unloaded;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        _favoritesChangedHandler = (s, e) =>
        {
            _ = RefreshFavoriteIdsAsync();
        };
        _libraryService.FavoritesChanged += _favoritesChangedHandler;
    }

    private void SearchResultsPage_Unloaded(object sender, RoutedEventArgs e)
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
        catch { }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.CurrentPlayingTrackId) ||
            e.PropertyName == nameof(SearchViewModel.IsCurrentlyPlaying))
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

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = RefreshFavoriteIdsAsync();

        if (e.Parameter is string query)
        {
            SearchQueryText.Text = $"Results for \"{query}\"";
            await ViewModel.ExecuteSearchAsync(query);
            _ = RefreshFavoriteIdsAsync();
        }
    }

    private void ListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track)
        {
            ViewModel.PlayTrackCommand.Execute(track);
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
            fontIcon.Glyph = (isCurrent && isPlaying) ? "\uE769" : "\uE768";
        }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is Track track)
        {
            if (track.Id == ViewModel.CurrentPlayingTrackId)
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
                ViewModel.PlayTrackCommand.Execute(track);
            }
        }
    }

    private void AlbumGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Album album)
        {
            Frame.Navigate(typeof(EntityDetailPage), new EntityNavigationParameter(EntityType.Album, album.Id));
        }
    }

    private void ArtistGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Artist artist)
        {
            Frame.Navigate(typeof(EntityDetailPage), new EntityNavigationParameter(EntityType.Artist, artist.Id));
        }
    }

    private void PlaylistGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Playlist playlist)
        {
            Frame.Navigate(typeof(PlaylistDetailPage), playlist.Id);
        }
    }

    public Visibility VisibilityFromCount(int count)
    {
        return count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public Visibility NoResultsVisibility(int trackCount, int albumCount, int artistCount, int playlistCount)
    {
        return (trackCount == 0 && albumCount == 0 && artistCount == 0 && playlistCount == 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    public static string TrackCountText(int count) => $"{count} tracks";

    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "0:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private async void TrackRow_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not Track track) return;

        var libraryService = App.Services.GetRequiredService<ILibraryService>();
        var playlistService = App.Services.GetRequiredService<IPlaylistService>();
        var queueService = App.Services.GetRequiredService<IQueueService>();

        await Helpers.TrackContextMenu.ShowAsync(fe, track, libraryService, playlistService, queueService, this.XamlRoot);
    }

    private async void MoreMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Track track)
        {
            var libraryService = App.Services.GetRequiredService<ILibraryService>();
            var playlistService = App.Services.GetRequiredService<IPlaylistService>();
            var queueService = App.Services.GetRequiredService<IQueueService>();
            await Helpers.TrackContextMenu.ShowAsync(fe, track, libraryService, playlistService, queueService, this.XamlRoot);
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
