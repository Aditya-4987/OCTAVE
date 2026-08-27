using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace Octave_Desktop.Converters;

/// <summary>
/// Converts a boolean favorite state to a heart glyph for Segoe MDL2 Assets:
///   true  -> \uEB52 (HeartFill)
///   false -> \uEB51 (HeartOutline)
/// </summary>
public class FavoriteGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isFav = value is bool b && b;
        return isFav ? "\uEB52" : "\uEB51";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>
/// Converts a boolean favorite state to a heart foreground brush:
///   true  -> Vibrant red/coral (#FF4060)
///   false -> Theme-aware tertiary text fill
/// </summary>
public class FavoriteBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush FavoritedBrush = new(Windows.UI.Color.FromArgb(255, 255, 64, 96));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isFav = value is bool b && b;
        if (isFav)
        {
            return FavoritedBrush;
        }

        if (Application.Current.Resources.TryGetValue("TextFillColorTertiaryBrush", out object? brushObj) && brushObj is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>
/// Converts a boolean favorite state to an accessible tooltip string.
/// </summary>
public class FavoriteToolTipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isFav = value is bool b && b;
        return isFav ? "Remove from Favorites" : "Add to Favorites";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
