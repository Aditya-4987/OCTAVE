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

    public MainWindow()
    {
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();
        RootGrid.DataContext = this;

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
    }

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
                "Genres" => typeof(Views.GenresPage),
                "Playlists" => typeof(Views.PlaylistsPage),
                _ => null
            };

            if (targetPageType != null && ContentFrame.SourcePageType != targetPageType)
            {
                ContentFrame.Navigate(targetPageType);
            }
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
        else if (ContentFrame.SourcePageType == typeof(Views.GenresPage)) tag = "Genres";
        else if (ContentFrame.SourcePageType == typeof(Views.PlaylistsPage)) tag = "Playlists";
        else if (ContentFrame.SourcePageType == typeof(Views.PlaylistDetailPage)) tag = "Playlists";

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
                EntityType.Genre => "Genres",
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

    // Helper methods for XAML bindings
    public Visibility BoolToVisibility(bool isPlaying) => isPlaying ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BoolToVisibilityNegation(bool isPlaying) => isPlaying ? Visibility.Collapsed : Visibility.Visible;

    public string FormatSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "0:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    public Brush GetShuffleColor(bool isShuffle)
    {
        return isShuffle 
            ? new SolidColorBrush(Microsoft.UI.Colors.White) 
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
    }

    public Brush GetRepeatColor(RepeatMode mode)
    {
        return mode != RepeatMode.None 
            ? new SolidColorBrush(Microsoft.UI.Colors.White) 
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
    }

    public string GetRepeatGlyph(RepeatMode mode)
    {
        return mode == RepeatMode.Track ? "\uE8ED" : "\uE8EE";
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
    }

    private void PlaybackSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider slider && slider.FocusState == FocusState.Keyboard)
        {
            ViewModel.SeekPlaybackCommand.Execute(slider.Value);
        }
    }

    // ---- Keyboard shortcuts ----------------------------------------------
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