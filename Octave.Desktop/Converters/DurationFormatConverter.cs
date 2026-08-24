using Microsoft.UI.Xaml.Data;
using System;

namespace Octave_Desktop.Converters;

public class DurationFormatConverter : IValueConverter
{
    // TEST-03: delegate to the canonical Core formatter (the XAML-facing wrapper
    // stays here; the logic lives in one tested place).
    public static string FormatSeconds(double seconds) =>
        Octave.Core.Helpers.DurationFormatter.FormatSeconds(seconds);

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
