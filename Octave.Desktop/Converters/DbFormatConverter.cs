using Microsoft.UI.Xaml.Data;
using System;

namespace Octave_Desktop.Converters;

public class DbFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double gain || value is float gainFloat)
        {
            double g = value is float f ? f : (double)value;
            if (Math.Abs(g) < 0.1) return "0 dB";
            return $"{(g > 0 ? "+" : "")}{g:F1} dB";
        }
        return "0 dB";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
