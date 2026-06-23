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
            ContentFrame.Navigate(typeof(Views.SettingsPage));
        }
        else if (args.InvokedItemContainer is NavigationViewItem item)
        {
            switch (item.Tag?.ToString())
            {
                case "Library":
                    ContentFrame.Navigate(typeof(Views.LibraryPage));
                    break;
                case "Albums":
                    ContentFrame.Navigate(typeof(Views.AlbumsPage));
                    break;
                case "Artists":
                    ContentFrame.Navigate(typeof(Views.ArtistsPage));
                    break;
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