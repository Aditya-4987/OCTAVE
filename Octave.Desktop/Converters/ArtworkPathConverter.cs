using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
using System.IO;

namespace Octave_Desktop.Converters;

public class ArtworkPathConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        try
        {
            string? artworkUrl = value as string;
            string parameterString = parameter as string ?? string.Empty;
            bool isArtist = parameterString.Equals("Artist", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(artworkUrl))
            {
                string fallback = isArtist ? "ms-appx:///Assets/PlaceholderArtist.png" : "ms-appx:///Assets/PlaceholderAlbum.png";
                return new BitmapImage(new Uri(fallback));
            }

            if (artworkUrl.StartsWith("ArtworkCache/", StringComparison.OrdinalIgnoreCase))
            {
                string absolutePath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, artworkUrl.Replace('/', '\\'));
                if (File.Exists(absolutePath))
                {
                    return new BitmapImage(new Uri(absolutePath));
                }
            }
            else if (artworkUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) || 
                     artworkUrl.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase))
            {
                return new BitmapImage(new Uri(artworkUrl));
            }

            return new BitmapImage(new Uri("ms-appx:///Assets/PlaceholderAlbum.png"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtworkPathConverter] {ex}");
            return new BitmapImage(new Uri("ms-appx:///Assets/PlaceholderAlbum.png"));
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
