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

        // NF-29: artwork here decoded at the MAIN window's rasterization scale -
        // wrong whenever this compact window sits on a monitor with a different
        // DPI. This window's converter instance now measures ITS OWN XamlRoot.
        // WinUI Window has no Resources of its own - the converter instance lives
        // in the content root's resource dictionary.
        if (RootGrid.Resources["ArtworkPathConverter"] is Converters.ArtworkPathConverter converter)
        {
            converter.ScaleProvider = () => RootGrid?.XamlRoot?.RasterizationScale ?? 1.0;
        }

        // NF-28: and re-decodes its artwork when dragged across DPI boundaries.
        Converters.ArtworkPathConverter.AttachDisplayScaleMonitor(RootGrid);

        // NF-32: same deterministic position-change signal as the main window.
        AppWindow.Changed += OnAppWindowChangedForScale;

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

    private void OnAppWindowChangedForScale(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange && RootGrid != null)
        {
            Converters.ArtworkPathConverter.CheckDisplayScale(RootGrid);
        }
    }

    // Bound helpers (mirrors MainWindow's play/pause visibility toggling).
    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BoolToVisibilityNegation(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public string FormatSeconds(double seconds) =>
        Octave.Core.Helpers.DurationFormatter.FormatSeconds(seconds);

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
