using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;
using Octave.Core.Interfaces;
using System;

namespace Octave_Desktop.Views;

public sealed partial class PlaylistDetailPage : Page
{
    public PlaylistDetailViewModel ViewModel { get; }

    private readonly System.Collections.Generic.List<Button> _playButtons = new();

    public PlaylistDetailPage()
    {
        ViewModel = App.Services.GetRequiredService<PlaylistDetailViewModel>();
        InitializeComponent();
        this.Unloaded += PlaylistDetailPage_Unloaded;

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string playlistId)
        {
            await ViewModel.LoadAsync(playlistId);
        }
    }

    private void PlaylistDetailPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Cleanup();
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

    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "0:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}
