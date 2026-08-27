using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Octave.Desktop.Controls;

public sealed partial class SpectrumVisualizerControl : UserControl
{
    private const int BarCount = 36;
    private readonly List<Rectangle> _bars = new();
    private readonly Brush _unplayedBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
    private Brush? _accentBrush;

    public double BaselineOffset { get; set; } = 10;

    // HLP-01: UpdateSpectrum mutates UI-thread-affine properties (bar.Height,
    // Canvas.SetTop, bar.Fill) but its data source (FFT frames) can arrive from a
    // background thread. The control now owns the marshaling contract itself
    // instead of trusting each call site to remember.
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    public SpectrumVisualizerControl()
    {
        InitializeComponent();
        Loaded += SpectrumVisualizerControl_Loaded;
    }

    private void SpectrumVisualizerControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (Resources.TryGetValue("SystemControlHighlightAccentBrush", out var accentObj) && accentObj is Brush b)
        {
            _accentBrush = b;
        }
        else
        {
            _accentBrush = (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"];
        }

        EnsureBarsCreated();
        UpdateBarLayout();
    }

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateBarLayout();
    }

    private void EnsureBarsCreated()
    {
        if (_bars.Count == BarCount) return;

        WaveformCanvas.Children.Clear();
        _bars.Clear();

        for (int i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = 2.0,
                Height = 2.0,
                RadiusX = 1.0,
                RadiusY = 1.0,
                Fill = _unplayedBrush
            };

            _bars.Add(bar);
            WaveformCanvas.Children.Add(bar);
        }
    }

    private void UpdateBarLayout()
    {
        EnsureBarsCreated();

        double canvasWidth = WaveformCanvas.ActualWidth;
        double canvasHeight = WaveformCanvas.ActualHeight;

        if (canvasWidth <= 0 || canvasHeight <= 0 || _bars.Count == 0) return;

        double spacing = 2.0;
        double totalSpacing = spacing * (BarCount - 1);
        double barWidth = Math.Max(1.0, (canvasWidth - totalSpacing) / BarCount);
        double baseline = canvasHeight - BaselineOffset;

        for (int i = 0; i < _bars.Count; i++)
        {
            var bar = _bars[i];
            bar.Width = barWidth;
            double left = i * (barWidth + spacing);
            Canvas.SetLeft(bar, left);
            Canvas.SetTop(bar, baseline - bar.Height);
        }
    }

    public void UpdateSpectrum(float[] fftData, double progressRatio)
    {
        if (_dispatcher.HasThreadAccess)
        {
            RenderSpectrum(fftData, progressRatio);
        }
        else
        {
            _dispatcher.TryEnqueue(() => RenderSpectrum(fftData, progressRatio));
        }
    }

    private void RenderSpectrum(float[] fftData, double progressRatio)
    {
        if (Visibility != Visibility.Visible) return;
        double canvasHeight = WaveformCanvas.ActualHeight;
        if (canvasHeight <= 0 || _bars.Count == 0) return;

        _accentBrush ??= (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"];
        Brush activeAccent = _accentBrush;
        double clampedRatio = Math.Clamp(progressRatio, 0.0, 1.0);
        double baseline = canvasHeight - BaselineOffset;
        double maxAvailableHeight = Math.Max(2.0, canvasHeight - BaselineOffset);

        for (int i = 0; i < _bars.Count; i++)
        {
            float amp = (fftData != null && i < fftData.Length) ? fftData[i] : 0.02f;
            double barHeight = Math.Clamp(amp * maxAvailableHeight, 2.0, maxAvailableHeight);

            var bar = _bars[i];
            bar.Height = barHeight;
            Canvas.SetTop(bar, baseline - barHeight); // Rises upwards (+ve gain) precisely from baseline

            double barFraction = (double)i / _bars.Count;
            bar.Fill = (barFraction <= clampedRatio) ? activeAccent : _unplayedBrush;
        }
    }

    public void ResetBars()
    {
        if (_bars.Count == 0) return;
        double canvasHeight = WaveformCanvas.ActualHeight;
        double baseline = canvasHeight - BaselineOffset;
        for (int i = 0; i < _bars.Count; i++)
        {
            var bar = _bars[i];
            bar.Height = 2.0;
            if (canvasHeight > 0)
            {
                Canvas.SetTop(bar, baseline - 2.0);
            }
            bar.Fill = _unplayedBrush;
        }
    }
}
