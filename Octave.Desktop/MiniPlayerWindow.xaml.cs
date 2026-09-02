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
    private readonly ToolTip _miniSeekTooltip = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Top };
    private readonly TextBlock _miniSeekTooltipText = new() { FontSize = 12 };
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

        _miniSeekTooltip.Content = _miniSeekTooltipText;
        ToolTipService.SetToolTip(MiniSeek, _miniSeekTooltip);

        MiniSeek.AddHandler(UIElement.PointerEnteredEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(MiniSeek_PointerEntered), true);
        MiniSeek.AddHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(MiniSeek_PointerMoved), true);
        MiniSeek.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(MiniSeek_PointerPressed), true);
        MiniSeek.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(MiniSeek_PointerReleased), true);
        MiniSeek.AddHandler(UIElement.PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(MiniSeek_PointerCaptureLost), true);
        MiniSeek.AddHandler(UIElement.PointerCanceledEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(MiniSeek_PointerCanceled), true);
        MiniSeek.AddHandler(UIElement.PointerExitedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(MiniSeek_PointerExited), true);
        MiniSeek.AddHandler(UIElement.PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(MiniSeek_PointerWheelChanged), true);
        MiniSeek.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(MiniSeek_KeyDown), true);

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

    private void UpdateMiniSeekTooltip(Slider? slider, double? cursorX = null)
    {
        if (slider == null)
        {
            return;
        }

        if (ViewModel.DurationSeconds <= 0)
        {
            _miniSeekTooltipText.Text = "No track loaded";
            _miniSeekTooltip.IsOpen = false;
            return;
        }

        double target;
        if (ViewModel.IsDragging)
        {
            target = Math.Clamp(slider.Value, 0.0, ViewModel.DurationSeconds);
        }
        else if (cursorX.HasValue && slider.ActualWidth > 0)
        {
            double ratio = Math.Clamp(cursorX.Value / slider.ActualWidth, 0.0, 1.0);
            target = ratio * ViewModel.DurationSeconds;
        }
        else
        {
            target = Math.Clamp(slider.Value, 0.0, ViewModel.DurationSeconds);
        }

        _miniSeekTooltipText.Text = $"Seek to {FormatSeconds(target)} / {FormatSeconds(ViewModel.DurationSeconds)}";
    }

    private void MiniSeek_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            var point = e.GetCurrentPoint(slider);
            UpdateMiniSeekTooltip(slider, point.Position.X);
            _miniSeekTooltip.IsOpen = true;
        }
    }

    private void MiniSeek_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            var point = e.GetCurrentPoint(slider);
            UpdateMiniSeekTooltip(slider, point.Position.X);
            _miniSeekTooltip.IsOpen = true;
        }
    }

    private void MiniSeek_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ViewModel.IsDragging = true;
        if (sender is Slider slider)
        {
            var point = e.GetCurrentPoint(slider);
            UpdateMiniSeekTooltip(slider, point.Position.X);
        }
        _miniSeekTooltip.IsOpen = true;
    }

    private void MiniSeek_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            ViewModel.SeekPlaybackCommand.Execute(slider.Value);
            UpdateMiniSeekTooltip(slider);
        }
        ViewModel.IsDragging = false;
        _miniSeekTooltip.IsOpen = false;
    }

    private void MiniSeek_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (ViewModel.IsDragging && sender is Slider slider)
        {
            ViewModel.SeekPlaybackCommand.Execute(slider.Value);
        }
        ViewModel.IsDragging = false;
        _miniSeekTooltip.IsOpen = false;
    }

    private void MiniSeek_PointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (ViewModel.IsDragging && sender is Slider slider)
        {
            ViewModel.SeekPlaybackCommand.Execute(slider.Value);
        }
        ViewModel.IsDragging = false;
        _miniSeekTooltip.IsOpen = false;
    }

    private void MiniSeek_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsDragging)
        {
            _miniSeekTooltip.IsOpen = false;
        }
    }

    private void MiniSeek_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Slider slider && ViewModel.DurationSeconds > 0)
        {
            var delta = e.GetCurrentPoint(slider).Properties.MouseWheelDelta;
            double step = 5.0; // 5 seconds standard step

            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            if (ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                step = 1.0; // 1 second fine-tune step
            }

            double change = (delta > 0) ? step : -step;
            ViewModel.SeekPlaybackByDeltaCommand.Execute(change);
            e.Handled = true;
        }
    }

    private void MiniSeek_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        bool shiftDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Home:
                ViewModel.SeekPlaybackCommand.Execute(0.0);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.End:
                ViewModel.SeekPlaybackCommand.Execute(ViewModel.DurationSeconds);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.PageUp:
                ViewModel.SeekPlaybackByDeltaCommand.Execute(10.0);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.PageDown:
                ViewModel.SeekPlaybackByDeltaCommand.Execute(-10.0);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Left when shiftDown:
                ViewModel.SeekPlaybackByDeltaCommand.Execute(-1.0);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right when shiftDown:
                ViewModel.SeekPlaybackByDeltaCommand.Execute(1.0);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Left:
                ViewModel.SeekPlaybackByDeltaCommand.Execute(-5.0);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right:
                ViewModel.SeekPlaybackByDeltaCommand.Execute(5.0);
                e.Handled = true;
                break;
        }
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
