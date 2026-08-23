using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Windows.Graphics;
using System;

namespace Octave_Desktop;

public sealed partial class MiniPlayerWindow : Window
{
    public ShellViewModel ViewModel { get; }

    private readonly Window _mainWindow;
    private bool _restored;

    public MiniPlayerWindow(Window mainWindow)
    {
        _mainWindow = mainWindow;
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();
        RootGrid.DataContext = this; // for the {Binding} artwork image

        // UI-MP-03: track the app's saved theme, not whatever the OS is set to
        // (mirrors MainWindow's RequestedTheme assignment).
        RootGrid.RequestedTheme = ThemeHelper.GetSavedTheme();

        // Compact, always-on-top, non-resizable overlay.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
        }
        AppWindow.Resize(new SizeInt32(460, 180));

        // Drag-to-seek on the mini seek bar (same pattern as the main window).
        MiniSeek.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(MiniSeek_PointerPressed), true);
        MiniSeek.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(MiniSeek_PointerReleased), true);

        this.Closed += MiniPlayerWindow_Closed;
        RootGrid.PreviewKeyDown += RootGrid_PreviewKeyDown;
    }

    private void RootGrid_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Space)
        {
            e.Handled = true;
            ViewModel.TogglePlayPauseCommand.Execute(null);
        }
    }

    // Bound helpers (mirrors MainWindow's play/pause visibility toggling).
    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BoolToVisibilityNegation(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public string FormatSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "0:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private void MiniSeek_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ViewModel.IsDragging = true;
    }

    private void MiniSeek_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            ViewModel.SeekPlaybackCommand.Execute(slider.Value);
        }
        ViewModel.IsDragging = false;
    }

    private void MiniSeek_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ViewModel.IsDragging = false;
    }

    private void MiniSeek_PointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ViewModel.IsDragging = false;
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        ShowMain();
        this.Close();
    }

    private void MiniPlayerWindow_Closed(object sender, WindowEventArgs args)
    {
        // If the user closes the mini window directly, bring the main one back
        // so the app isn't left with no visible window.
        ShowMain();
    }

    private void ShowMain()
    {
        if (_restored) return;
        _restored = true;
        _mainWindow.AppWindow.Show();
        _mainWindow.Activate();
    }
}
