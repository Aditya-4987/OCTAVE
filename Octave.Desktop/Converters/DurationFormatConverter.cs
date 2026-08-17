using Microsoft.UI.Xaml.Data;
using System;

namespace Octave_Desktop.Converters;

public class DurationFormatConverter : IValueConverter
{
    public static string FormatSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0) return "0:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double seconds)
        {
            return FormatSeconds(seconds);
        }
        if (value is float secondsFloat)
        {
            return FormatSeconds(secondsFloat);
        }
        if (value is int secondsInt)
        {
            return FormatSeconds(secondsInt);
        }
        return "0:00";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
