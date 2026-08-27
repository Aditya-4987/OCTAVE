using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;
using System;

namespace Octave_Desktop.Views;

public sealed partial class EntityDetailPage : Page
{
    public EntityDetailViewModel ViewModel { get; }

    private readonly System.Collections.Generic.List<Button> _playButtons = new();
    private readonly System.Collections.Generic.List<Button> _favoriteButtons = new();
    private readonly System.Collections.Generic.HashSet<string> _favoriteTrackIds = new(StringComparer.Ordinal);
    private readonly Octave.Core.Services.Library.ILibraryService _libraryService;
    private readonly EventHandler _favoritesChangedHandler;

    public EntityDetailPage()
    {
        ViewModel = App.Services.GetRequiredService<EntityDetailViewModel>();
        _libraryService = App.Services.GetRequiredService<Octave.Core.Services.Library.ILibraryService>();
        InitializeComponent();
        this.Unloaded += EntityDetailPage_Unloaded;

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        _favoritesChangedHandler = (s, e) =>
        {
            _ = RefreshFavoriteIdsAsync();
        };
        _libraryService.FavoritesChanged += _favoritesChangedHandler;
    }

    private void EntityDetailPage_Unloaded(object sender, RoutedEventArgs e)
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
        if (e.PropertyName == nameof(EntityDetailViewModel.CurrentPlayingTrackId) ||
            e.PropertyName == nameof(EntityDetailViewModel.IsCurrentlyPlaying))
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

        if (e.Parameter is EntityNavigationParameter param)
        {
            await ViewModel.LoadEntityAsync(param);
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

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "0:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    public static Visibility HasTextVisibility(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private async void TrackRow_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not Track track) return;

        var libraryService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Octave.Core.Services.Library.ILibraryService>(App.Services);
        var playlistService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Octave.Core.Interfaces.IPlaylistService>(App.Services);
        var queueService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Octave.Core.Interfaces.IQueueService>(App.Services);

        await Helpers.TrackContextMenu.ShowAsync(fe, track, libraryService, playlistService, queueService, this.XamlRoot);
    }

    private async void MoreMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not Track track) return;

        var libraryService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Octave.Core.Services.Library.ILibraryService>(App.Services);
        var playlistService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Octave.Core.Interfaces.IPlaylistService>(App.Services);
        var queueService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Octave.Core.Interfaces.IQueueService>(App.Services);

        await Helpers.TrackContextMenu.ShowAsync(fe, track, libraryService, playlistService, queueService, this.XamlRoot);
    }
}
