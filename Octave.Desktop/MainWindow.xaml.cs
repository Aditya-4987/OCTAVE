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

        // Default navigation
        ContentFrame.Navigated += ContentFrame_Navigated;
        ContentFrame.Navigate(typeof(Views.LibraryPage));
        NavView.SelectedItem = LibraryItem;

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
                "Library" => typeof(Views.LibraryPage),
                "Albums" => typeof(Views.AlbumsPage),
                "Artists" => typeof(Views.ArtistsPage),
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
        if (ContentFrame.SourcePageType == typeof(Views.LibraryPage)) tag = "Library";
        else if (ContentFrame.SourcePageType == typeof(Views.AlbumsPage)) tag = "Albums";
        else if (ContentFrame.SourcePageType == typeof(Views.ArtistsPage)) tag = "Artists";

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
            string parentTag = param.Type == EntityType.Album ? "Albums" : "Artists";
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
        return time.ToString(@"m\:ss");
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
}