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
        if (sender is not FrameworkElement fe || fe.DataContext is not Track track) return;

        var flyout = new MenuFlyout();

        // Favorite toggle.
        bool isFavorite = await _libraryService.IsFavoriteAsync(track.Id);
        var favItem = new MenuFlyoutItem { Text = isFavorite ? "Remove from favorites" : "Add to favorites" };
        favItem.Click += async (s, a) =>
        {
            try { await _libraryService.ToggleFavoriteAsync(track.Id); }
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine($"[Library] Favorite toggle failed: {ex}"); }
        };
        flyout.Items.Add(favItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var playlists = await _playlistService.GetPlaylistsAsync();
        foreach (var playlist in playlists)
        {
            string playlistId = playlist.Id;
            var item = new MenuFlyoutItem { Text = playlist.Title };
            item.Click += async (s, a) =>
            {
                try { await _playlistService.AddTrackAsync(playlistId, track.Id); }
                catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine($"[Library] Add to playlist failed: {ex}"); }
            };
            flyout.Items.Add(item);
        }
        if (playlists.Count > 0) flyout.Items.Add(new MenuFlyoutSeparator());

        var newItem = new MenuFlyoutItem { Text = "New playlist…" };
        newItem.Click += async (s, a) => await CreatePlaylistWithTrackAsync(track.Id);
        flyout.Items.Add(newItem);

        flyout.ShowAt(fe, e.GetPosition(fe));
    }

    private async Task CreatePlaylistWithTrackAsync(string trackId)
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
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
            {
                var playlist = await _playlistService.CreatePlaylistAsync(input.Text.Trim());
                await _playlistService.AddTrackAsync(playlist.Id, trackId);
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Library] Create playlist dialog failed: {ex}");
        }
    }

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

    private void ListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Octave.Core.Models.Track track)
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
        if (btn.Content is FontIcon fontIcon && btn.DataContext is Octave.Core.Models.Track track)
        {
            bool isCurrent = track.Id == ViewModel.CurrentPlayingTrackId;
            bool isPlaying = ViewModel.IsCurrentlyPlaying;
            fontIcon.Glyph = (isCurrent && isPlaying) ? "\uE769" : "\uE768";
        }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is Octave.Core.Models.Track track)
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

    public static string GetProviderGlyph(string provider)
    {
        return provider.Equals("Local", StringComparison.OrdinalIgnoreCase) ? "\uE770" : "\uE774";
    }

    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "0:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}
