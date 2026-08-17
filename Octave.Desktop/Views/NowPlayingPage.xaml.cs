using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Octave.Core.Models;
using Octave_Desktop.ViewModels;

namespace Octave_Desktop.Views;

public sealed partial class NowPlayingPage : Page
{
    public NowPlayingViewModel ViewModel { get; private set; }
    private Storyboard? _currentLyricsAnimation;
    private Storyboard? _currentQueueAnimation;

    public NowPlayingPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<NowPlayingViewModel>();
        DataContext = ViewModel;

        Loaded += NowPlayingPage_Loaded;
        Unloaded += NowPlayingPage_Unloaded;
    }

    private void NowPlayingPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SubscribeEvents();
        ViewModel.RefreshState();

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        AnimateLyricsTransition(ViewModel.IsLyricsPanelVisible, immediate: true);
        AnimateQueueTransition(ViewModel.IsQueuePanelVisible, immediate: true);
    }

    private void NowPlayingPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _currentLyricsAnimation?.Stop();
        _currentQueueAnimation?.Stop();
        ViewModel.Dispose();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsLyricsPanelVisible))
        {
            AnimateLyricsTransition(ViewModel.IsLyricsPanelVisible, immediate: false);
        }
        else if (e.PropertyName == nameof(ViewModel.IsQueuePanelVisible))
        {
            AnimateQueueTransition(ViewModel.IsQueuePanelVisible, immediate: false);
        }
    }

    private void TopStageGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Handled dynamically via Grid column widths
    }

    private void AnimateLyricsTransition(bool showLyrics, bool immediate = false)
    {
        _currentLyricsAnimation?.Stop();

        double targetLyricsX = showLyrics ? 0 : 350;
        double targetLyricsOpacity = showLyrics ? 1.0 : 0.0;

        if (immediate)
        {
            AlbumPanelTransform.X = 0;
            LyricsPanelTransform.X = targetLyricsX;
            LyricsPanelControl.Opacity = targetLyricsOpacity;
            LyricsPanelControl.Visibility = showLyrics ? Visibility.Visible : Visibility.Collapsed;
            UpdateLyricsColumnWidth(showLyrics);
            return;
        }

        if (showLyrics)
        {
            UpdateLyricsColumnWidth(true);
            LyricsPanelControl.Visibility = Visibility.Visible;
        }

        var sb = new Storyboard();
        TimeSpan duration = TimeSpan.FromMilliseconds(400);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        // 1. Lyrics Panel Right <-> In translation
        var lyricsXAnim = new DoubleAnimation
        {
            To = targetLyricsX,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(lyricsXAnim, LyricsPanelTransform);
        Storyboard.SetTargetProperty(lyricsXAnim, "X");
        sb.Children.Add(lyricsXAnim);

        // 2. Lyrics Panel Opacity fade
        var lyricsOpacityAnim = new DoubleAnimation
        {
            To = targetLyricsOpacity,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(lyricsOpacityAnim, LyricsPanelControl);
        Storyboard.SetTargetProperty(lyricsOpacityAnim, "Opacity");
        sb.Children.Add(lyricsOpacityAnim);

        sb.Completed += (s, e) =>
        {
            if (!showLyrics)
            {
                LyricsPanelControl.Visibility = Visibility.Collapsed;
                UpdateLyricsColumnWidth(false);
            }
            AlbumPanelTransform.X = 0;
        };

        _currentLyricsAnimation = sb;
        sb.Begin();
    }

    private void UpdateLyricsColumnWidth(bool showLyrics)
    {
        if (showLyrics)
        {
            AlbumColumn.Width = new GridLength(1, GridUnitType.Star);
            LyricsColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            AlbumColumn.Width = new GridLength(1, GridUnitType.Star);
            LyricsColumn.Width = new GridLength(0, GridUnitType.Pixel);
        }
    }

    private void AnimateQueueTransition(bool showQueue, bool immediate = false)
    {
        _currentQueueAnimation?.Stop();

        double targetQueueX = showQueue ? 0 : 350;
        double targetQueueOpacity = showQueue ? 1.0 : 0.0;

        if (immediate)
        {
            QueuePanelTransform.X = targetQueueX;
            QueuePanelControl.Opacity = targetQueueOpacity;
            QueuePanelControl.Visibility = showQueue ? Visibility.Visible : Visibility.Collapsed;
            UpdateQueueColumnWidth(showQueue);
            return;
        }

        if (showQueue)
        {
            UpdateQueueColumnWidth(true);
            QueuePanelControl.Visibility = Visibility.Visible;
        }

        var sb = new Storyboard();
        TimeSpan duration = TimeSpan.FromMilliseconds(400);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        // Queue Panel X Animation
        var queueXAnim = new DoubleAnimation
        {
            To = targetQueueX,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(queueXAnim, QueuePanelTransform);
        Storyboard.SetTargetProperty(queueXAnim, "X");
        sb.Children.Add(queueXAnim);

        // Queue Panel Opacity Fade
        var queueOpacityAnim = new DoubleAnimation
        {
            To = targetQueueOpacity,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(queueOpacityAnim, QueuePanelControl);
        Storyboard.SetTargetProperty(queueOpacityAnim, "Opacity");
        sb.Children.Add(queueOpacityAnim);

        sb.Completed += (s, e) =>
        {
            if (!showQueue)
            {
                QueuePanelControl.Visibility = Visibility.Collapsed;
                UpdateQueueColumnWidth(false);
            }
        };

        _currentQueueAnimation = sb;
        sb.Begin();
    }

    private void UpdateQueueColumnWidth(bool showQueue)
    {
        if (showQueue)
        {
            CreditsColumn.Width = new GridLength(1, GridUnitType.Star);
            QueueColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            CreditsColumn.Width = new GridLength(1, GridUnitType.Star);
            QueueColumn.Width = new GridLength(0, GridUnitType.Pixel);
        }
    }

    private void CreditsPanelControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height > 0)
        {
            // Give QueuePanel a generous minimum height of 400px to prevent cramped views with short credits
            QueuePanelControl.MaxHeight = Math.Max(400, e.NewSize.Height);
        }
    }

    private void AlbumPanelControl_PlayPauseRequested(object sender, RoutedEventArgs e)
    {
        ViewModel.TogglePlayPauseCommand.Execute(null);
    }

    private void QueuePanelControl_PlayItemRequested(object sender, QueueItem item)
    {
        ViewModel.PlayQueueItemCommand.Execute(item);
    }

    private async void CreditsPanelControl_ArtistClicked(object sender, ArtistDisplayItem artist)
    {
        string? artistId = await ViewModel.ResolveArtistIdAsync(artist);
        if (!string.IsNullOrWhiteSpace(artistId))
        {
            Frame?.Navigate(typeof(EntityDetailPage), new EntityNavigationParameter(EntityType.Artist, artistId));
        }
    }

    private async void CreditsPanelControl_AlbumClicked(object sender, RoutedEventArgs e)
    {
        string? albumId = await ViewModel.ResolveAlbumIdAsync();
        if (!string.IsNullOrWhiteSpace(albumId))
        {
            Frame?.Navigate(typeof(EntityDetailPage), new EntityNavigationParameter(EntityType.Album, albumId));
        }
    }

    private void QueuePanelControl_ClearQueueRequested(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearQueueCommand.Execute(null);
    }
}
