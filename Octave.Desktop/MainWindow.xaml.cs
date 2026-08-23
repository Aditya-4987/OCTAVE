using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;
using System;

namespace Octave_Desktop;

public sealed partial class MainWindow : Window
{
    public ShellViewModel ViewModel { get; }
    private readonly Octave.Core.Services.Audio.IAudioPlayerService _audioPlayer = App.Services.GetRequiredService<Octave.Core.Services.Audio.IAudioPlayerService>();

    public MainWindow()
    {
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();
        RootGrid.DataContext = this;

        Octave_Desktop.Helpers.WindowMinSizeHelper.SetMinSize(this, 750, 500);

        RootGrid.SizeChanged += RootGrid_SizeChanged;

        // Apply the saved theme to the window root.
        RootGrid.RequestedTheme = ThemeHelper.GetSavedTheme();

        // Default navigation
        ContentFrame.Navigated += ContentFrame_Navigated;
        ContentFrame.Navigate(typeof(Views.HomePage));
        NavView.SelectedItem = HomeItem;

        // Register pointer and manipulation handlers with handledEventsToo = true
        PlaybackSlider.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(PlaybackSlider_PointerPressed), true);
        PlaybackSlider.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(PlaybackSlider_PointerReleased), true);
        PlaybackSlider.AddHandler(UIElement.ManipulationStartedEvent, new Microsoft.UI.Xaml.Input.ManipulationStartedEventHandler(PlaybackSlider_ManipulationStarted), true);
        PlaybackSlider.AddHandler(UIElement.ManipulationCompletedEvent, new Microsoft.UI.Xaml.Input.ManipulationCompletedEventHandler(PlaybackSlider_ManipulationCompleted), true);

        // Global Spacebar Play/Pause handler before UI controls consume Space for selection
        RootGrid.PreviewKeyDown += RootGrid_PreviewKeyDown;

        // Real-time audio spectrum rendering tick
        CompositionTarget.Rendering += CompositionTarget_Rendering;

        // SYS-05/NF-11: process-level hooks previously outlived the window they
        // were bound to - the SMTC subscriptions kept the OS reacting to a dead
        // hwnd, the comctl32 size-subclass stayed installed, and the
        // per-frame rendering callback was never detached.
        Closed += (s, e) =>
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            Octave_Desktop.Helpers.WindowMinSizeHelper.ClearMinSize(WinRT.Interop.WindowNative.GetWindowHandle(this));
            App.Services.GetRequiredService<Octave_Desktop.Services.System.ISmtcService>().Dispose();
        };
    }

    private void CompositionTarget_Rendering(object? sender, object e)
    {
        if (ProgressBarVisualizer == null || !ViewModel.IsVisualizerEnabled || ProgressBarVisualizer.Visibility != Visibility.Visible) return;
        if (!ViewModel.IsPlaying) return;

        var fft = _audioPlayer.GetFftData(36);
        double ratio = (ViewModel.DurationSeconds > 0) ? (ViewModel.PositionSeconds / ViewModel.DurationSeconds) : 0.0;
        ProgressBarVisualizer.UpdateSpectrum(fft, ratio);
    }

    private void RootGrid_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 950)
        {
            ExtraControlsPanel.Visibility = Visibility.Collapsed;
            MoreOptionsButton.Visibility = Visibility.Visible;
            TrackDetailsColumn.MaxWidth = 200;
        }
        else
        {
            ExtraControlsPanel.Visibility = Visibility.Visible;
            MoreOptionsButton.Visibility = Visibility.Collapsed;
            TrackDetailsColumn.MaxWidth = 350;
        }
    }

    public string GetNowPlayingGlyph(bool isPlaying) => isPlaying ? "\uE769" : "\uE768";

    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            if (ContentFrame.SourcePageType != typeof(Views.SettingsPage))
            {
                ContentFrame.Navigate(typeof(Views.SettingsPage));
            }
        }
        else if (args.InvokedItemContainer is NavigationViewItem item)
        {
            Type? targetPageType = item.Tag?.ToString() switch
            {
                "Home" => typeof(Views.HomePage),
                "Library" => typeof(Views.LibraryPage),
                "Albums" => typeof(Views.AlbumsPage),
                "Artists" => typeof(Views.ArtistsPage),
                "Playlists" => typeof(Views.PlaylistsPage),
                "NowPlaying" => typeof(Views.NowPlayingPage),
                "AudioFX" => typeof(Views.AudioFxPage),
                _ => null
            };

            if (targetPageType != null && ContentFrame.SourcePageType != targetPageType)
            {
                ContentFrame.Navigate(targetPageType);
            }
        }
    }

    private void TrackDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsNowPlayingOpen)
        {
            ViewModel.IsNowPlayingOpen = true;
        }
        
        // Select Info tab (index 0)
        SidebarPivot.SelectedIndex = 0;
    }

    private void QueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsNowPlayingOpen && SidebarPivot.SelectedIndex == 1)
        {
            // Close if already open on Queue tab
            ViewModel.IsNowPlayingOpen = false;
        }
        else
        {
            // Open and select Queue tab (index 1)
            ViewModel.IsNowPlayingOpen = true;
            SidebarPivot.SelectedIndex = 1;
        }
    }

    private void NavigationView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        NavView.IsBackEnabled = ContentFrame.CanGoBack;

        if (ContentFrame.SourcePageType == typeof(Views.SettingsPage))
        {
            NavView.SelectedItem = NavView.SettingsItem;
            return;
        }

        string? tag = null;
        if (ContentFrame.SourcePageType == typeof(Views.HomePage)) tag = "Home";
        else if (ContentFrame.SourcePageType == typeof(Views.LibraryPage)) tag = "Library";
        else if (ContentFrame.SourcePageType == typeof(Views.AlbumsPage)) tag = "Albums";
        else if (ContentFrame.SourcePageType == typeof(Views.ArtistsPage)) tag = "Artists";
        else if (ContentFrame.SourcePageType == typeof(Views.PlaylistsPage)) tag = "Playlists";
        else if (ContentFrame.SourcePageType == typeof(Views.PlaylistDetailPage)) tag = "Playlists";
        else if (ContentFrame.SourcePageType == typeof(Views.NowPlayingPage)) tag = "NowPlaying";
        else if (ContentFrame.SourcePageType == typeof(Views.AudioFxPage)) tag = "AudioFX";

        if (tag != null)
        {
            foreach (var item in NavView.MenuItems)
            {
                if (item is NavigationViewItem navItem && navItem.Tag?.ToString() == tag)
                {
                    NavView.SelectedItem = navItem;
                    break;
                }
            }
        }
        else if (ContentFrame.SourcePageType == typeof(Views.EntityDetailPage) && e.Parameter is EntityNavigationParameter param)
        {
            string parentTag = param.Type switch
            {
                EntityType.Album => "Albums",
                _ => "Artists"
            };
            foreach (var item in NavView.MenuItems)
            {
                if (item is NavigationViewItem navItem && navItem.Tag?.ToString() == parentTag)
                {
                    NavView.SelectedItem = navItem;
                    break;
                }
            }
        }
    }

    private void BackgroundImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (sender is Image img)
        {
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0.0,
                To = 0.3,
                Duration = new Duration(TimeSpan.FromMilliseconds(500)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, img);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }
    }

    // Helper methods for XAML bindings
    public Visibility BoolToVisibility(bool isPlaying) => isPlaying ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BoolToVisibilityNegation(bool isPlaying) => isPlaying ? Visibility.Collapsed : Visibility.Visible;

    public string FormatSeconds(double seconds) => Converters.DurationFormatConverter.FormatSeconds(seconds);

    public Brush GetShuffleColor(bool isShuffle)
    {
        return isShuffle 
            ? new SolidColorBrush(Microsoft.UI.Colors.White) 
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
    }

    public Brush GetActiveColor(bool isActive)
    {
        return isActive ? (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"] : new SolidColorBrush(Microsoft.UI.Colors.White);
    }

    public Brush GetFavoriteColor(bool isFavorite)
    {
        return isFavorite ? (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"] : new SolidColorBrush(Microsoft.UI.Colors.White);
    }

    public Brush GetRepeatColor(RepeatMode mode)
    {
        return mode != RepeatMode.None 
            ? new SolidColorBrush(Microsoft.UI.Colors.White) 
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
    }

    public string GetRepeatGlyph(RepeatMode mode)
    {
        return mode == RepeatMode.Track ? "\uE8ED" : "\uE8EE"; // RepeatOne vs RepeatAll
    }

    public string GetFavoriteGlyph(bool isFavorite)
    {
        return isFavorite ? "\uEB52" : "\uEB51"; // Filled Heart vs Outline Heart
    }

    public string GetMuteGlyph(bool isMuted, double volume)
    {
        if (isMuted || volume <= 0.01)
        {
            return "\uE74F"; // Volume Muted
        }
        if (volume < 33.3)
        {
            return "\uE992"; // Volume Low
        }
        if (volume < 66.6)
        {
            return "\uE993"; // Volume Medium
        }
        return "\uE994"; // Volume High
    }

    private void VolumeSlider_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            var delta = e.GetCurrentPoint(slider).Properties.MouseWheelDelta;
            double step = 2.0; // standard: 2%
            
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            if (ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                step = 0.5; // fine adjustment: 0.5%
            }
            
            double change = (delta > 0) ? step : -step;
            slider.Value = Math.Clamp(slider.Value + change, 0, 100);
            e.Handled = true;
        }
    }

    private void PlaybackSlider_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ViewModel.IsDragging = true;
    }

    private void PlaybackSlider_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            ViewModel.SeekPlaybackCommand.Execute(slider.Value);
        }
        ViewModel.IsDragging = false;
    }

    private void PlaybackSlider_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ViewModel.IsDragging = false;
    }

    private void PlaybackSlider_PointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ViewModel.IsDragging = false;
    }

    private void PlaybackSlider_ManipulationStarted(object sender, Microsoft.UI.Xaml.Input.ManipulationStartedRoutedEventArgs e)
    {
        ViewModel.IsDragging = true;
    }

    private void PlaybackSlider_ManipulationCompleted(object sender, Microsoft.UI.Xaml.Input.ManipulationCompletedRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            ViewModel.SeekPlaybackCommand.Execute(slider.Value);
        }
        ViewModel.IsDragging = false;
    }

    private void PlaybackSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider slider && slider.FocusState == FocusState.Keyboard)
        {
            ViewModel.SeekPlaybackCommand.Execute(slider.Value);
        }
    }

    // ---- Keyboard shortcuts ----------------------------------------------
    private void RootGrid_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Space)
        {
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.Content?.XamlRoot);
            if (focused is TextBox || focused is AutoSuggestBox || focused is PasswordBox)
            {
                return; // Do not intercept spacebar when user is actively typing in a text field
            }

            // Intercept space globally so buttons, lists, cards, and sidebar items do not consume it for UI selection
            e.Handled = true;
            ViewModel.TogglePlayPauseCommand.Execute(null);
        }
    }

    private void Accel_PlayPause(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.TogglePlayPauseCommand.Execute(null);
        args.Handled = true;
    }

    private void Accel_Next(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.NextCommand.Execute(null);
        args.Handled = true;
    }

    private void Accel_Previous(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.PreviousCommand.Execute(null);
        args.Handled = true;
    }

    private void Accel_VolumeUp(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.VolumeUpCommand.Execute(null);
        args.Handled = true;
    }

    private void Accel_VolumeDown(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.VolumeDownCommand.Execute(null);
        args.Handled = true;
    }

    private void Accel_Mute(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.ToggleMuteCommand.Execute(null);
        args.Handled = true;
    }

    private void MiniPlayer_Click(object sender, RoutedEventArgs e)
    {
        var mini = new MiniPlayerWindow(this);
        mini.Activate();
        this.AppWindow.Hide(); // hide the main window while the mini overlay is up
    }

    private void QueueList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QueueItem item)
        {
            ViewModel.PlayQueueItemCommand.Execute(item);
        }
    }

    private void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is QueueItem item)
        {
            ViewModel.RemoveQueueItemCommand.Execute(item);
        }
    }

    public static string GetSuggestionGlyph(EntityType type)
    {
        return type switch
        {
            EntityType.Track => "\uE189",
            EntityType.Artist => "\uE77B",
            EntityType.Playlist => "\uE90B",
            _ => "\uE93C"
        };
    }

    private void GlobalSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _ = ViewModel.UpdateSearchSuggestionsAsync(sender.Text);
        }
    }

    private void GlobalSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is SearchSuggestion suggestion)
        {
            if (suggestion.Type == EntityType.Track)
            {
                _ = ViewModel.PlayTrackByIdAsync(suggestion.Id);
            }
            else if (suggestion.Type == EntityType.Playlist)
            {
                ContentFrame.Navigate(typeof(Views.PlaylistDetailPage), suggestion.Id);
            }
            else
            {
                ContentFrame.Navigate(typeof(Views.EntityDetailPage), new EntityNavigationParameter(suggestion.Type, suggestion.Id));
            }
        }
        else if (!string.IsNullOrWhiteSpace(args.QueryText))
        {
            ContentFrame.Navigate(typeof(Views.SearchResultsPage), args.QueryText);
        }
    }
}