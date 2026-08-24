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
