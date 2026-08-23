using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace Octave_Desktop.Converters;

/// <summary>
/// Batch 13 (NP-20/NP-21): drives the queue-row visuals off QueueItem.IsPlaying.
/// Implemented as a converter rather than an x:Bind *function* because the WinUI
/// markup compiler in use cannot compile function bindings inside a DataTemplate
/// (it dies in PageCodeGen with an internal error).
///
/// ConverterParameter selects the pair being resolved:
///   "Background" -> translucent accent tint when playing, transparent otherwise
///   "Foreground" -> accent when playing, primary text fill otherwise
/// Brushes are resolved from theme resources per call so light/dark switches
/// stay correct (NP-14 family - never cache a themed brush across themes).
/// </summary>
public class QueuePlayStateBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);
    private static Brush? _tintBrush;
    private static Windows.UI.Color _tintSourceColor;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isPlaying = value is bool b && b;
        string mode = parameter as string ?? "Background";

        if (!isPlaying)
        {
            return mode.Equals("Foreground", StringComparison.OrdinalIgnoreCase)
                ? ResolveBrush("TextFillColorPrimaryBrush", Microsoft.UI.Colors.White)
                : TransparentBrush;
        }

        return mode.Equals("Foreground", StringComparison.OrdinalIgnoreCase)
            ? ResolveBrush("SystemControlHighlightAccentBrush", Microsoft.UI.Colors.DodgerBlue)
            : GetTint();
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();

    private static Brush GetTint()
    {
        var accent = ResolveBrush("SystemControlHighlightAccentBrush", Microsoft.UI.Colors.DodgerBlue);
        Windows.UI.Color source = (accent is SolidColorBrush scb) ? scb.Color : Microsoft.UI.Colors.DodgerBlue;

        // Rebuild only when the themed accent actually changes (theme switch).
        if (_tintBrush == null || source != _tintSourceColor)
        {
            _tintSourceColor = source;
            _tintBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(30, source.R, source.G, source.B));
        }
        return _tintBrush;
    }

    private static Brush ResolveBrush(string resourceKey, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out object? value) && value is Brush b)
        {
            return b;
        }
        return new SolidColorBrush(fallback);
    }
}

/// <summary>
/// Generic conditional-value converter: returns TrueValue when the bound value is
/// true, FalseValue otherwise. Used for the play/pause glyph swap on the playing
/// queue row (a bool cannot bind straight into a glyph string).
/// </summary>
public class ConditionalValueConverter : IValueConverter
{
    public object TrueValue { get; set; } = string.Empty;
    public object FalseValue { get; set; } = string.Empty;

    public object Convert(object value, Type targetType, object parameter, string language) =>
        (value is bool b && b) ? TrueValue : FalseValue;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
