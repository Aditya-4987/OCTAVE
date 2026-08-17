using Microsoft.UI.Xaml.Data;
using System;

namespace Octave_Desktop.Converters;

public class DbFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double gain)
        {
            if (Math.Abs(gain) < 0.1) return "0 dB";
            return $"{(gain > 0 ? "+" : "")}{gain:F1} dB";
        }
        if (value is float gainFloat)
        {
            if (Math.Abs(gainFloat) < 0.1f) return "0 dB";
            return $"{(gainFloat > 0 ? "+" : "")}{gainFloat:F1} dB";
        }
        return "0 dB";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
