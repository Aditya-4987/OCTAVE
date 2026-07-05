using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;
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

    private void TrackRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not Track track) return;

        var flyout = new MenuFlyout();
        var remove = new MenuFlyoutItem { Text = "Remove from playlist" };
        remove.Click += (s, a) => ViewModel.RemoveTrackCommand.Execute(track);
        flyout.Items.Add(remove);
        flyout.ShowAt(fe, e.GetPosition(fe));
    }

    private void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.DeleteSelfCommand.Execute(null);
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    public static string GetProviderGlyph(string provider)
    {
        return provider.Equals("Local", StringComparison.OrdinalIgnoreCase) ? "" : "";
    }

    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "0:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}
