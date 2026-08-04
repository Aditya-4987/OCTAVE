using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
using System.IO;

namespace Octave_Desktop.Converters;

public class ArtworkPathConverter : IValueConverter
{
    private const int MaxCacheSize = 200;
    private static readonly object CacheLock = new();
    private static readonly System.Collections.Generic.Dictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Generic.LinkedList<string> LruList = new();


    public object Convert(object value, Type targetType, object parameter, string language)
    {
        try
        {
            string? artworkUrl = value as string;
            string parameterString = parameter as string ?? string.Empty;
            bool isArtist = parameterString.Equals("Artist", StringComparison.OrdinalIgnoreCase);

            string uriString;
            if (string.IsNullOrWhiteSpace(artworkUrl))
            {
                uriString = isArtist ? "ms-appx:///Assets/PlaceholderArtist.png" : "ms-appx:///Assets/PlaceholderAlbum.png";
            }
            else if (artworkUrl.StartsWith("ArtworkCache/", StringComparison.OrdinalIgnoreCase))
            {
                string absolutePath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, artworkUrl.Replace('/', '\\'));
                if (File.Exists(absolutePath))
                {
                    uriString = absolutePath;
                }
                else
                {
                    uriString = isArtist ? "ms-appx:///Assets/PlaceholderArtist.png" : "ms-appx:///Assets/PlaceholderAlbum.png";
                }
            }
            else if (artworkUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) || 
                     artworkUrl.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase))
            {
                uriString = artworkUrl;
            }
            else
            {
                uriString = isArtist ? "ms-appx:///Assets/PlaceholderArtist.png" : "ms-appx:///Assets/PlaceholderAlbum.png";
            }

            int targetDecodeWidth = 320;
            if (!string.IsNullOrWhiteSpace(parameterString))
            {
                if (parameterString.Equals("Small", StringComparison.OrdinalIgnoreCase) || 
                    parameterString.Equals("Thumb", StringComparison.OrdinalIgnoreCase) ||
                    parameterString.Equals("56", StringComparison.OrdinalIgnoreCase) ||
                    parameterString.Equals("40", StringComparison.OrdinalIgnoreCase))
                {
                    targetDecodeWidth = 112;
                }
                else if (parameterString.Equals("Artist", StringComparison.OrdinalIgnoreCase) ||
                         parameterString.Equals("Card", StringComparison.OrdinalIgnoreCase) ||
                         parameterString.Equals("Medium", StringComparison.OrdinalIgnoreCase) ||
                         parameterString.Equals("160", StringComparison.OrdinalIgnoreCase))
                {
                    targetDecodeWidth = 320;
                }
                else if (parameterString.Equals("Large", StringComparison.OrdinalIgnoreCase) ||
                         parameterString.Equals("NowPlaying", StringComparison.OrdinalIgnoreCase) ||
                         parameterString.Equals("Background", StringComparison.OrdinalIgnoreCase) ||
                         parameterString.Equals("500", StringComparison.OrdinalIgnoreCase))
                {
                    targetDecodeWidth = 600;
                }
                else if (int.TryParse(parameterString, out int customWidth) && customWidth > 0)
                {
                    targetDecodeWidth = customWidth;
                }
            }

            string cacheKey = $"{uriString}_{targetDecodeWidth}";

            lock (CacheLock)
            {
                if (Cache.TryGetValue(cacheKey, out var cachedImage))
                {
                    LruList.Remove(cacheKey);
                    LruList.AddFirst(cacheKey);
                    return cachedImage;
                }

                var newImage = new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Logical,
                    DecodePixelWidth = targetDecodeWidth,
                    UriSource = new Uri(uriString)
                };

                if (Cache.Count >= MaxCacheSize && LruList.Last != null)
                {
                    string oldestKey = LruList.Last.Value;
                    LruList.RemoveLast();
                    Cache.Remove(oldestKey);
                }

                Cache[cacheKey] = newImage;
                LruList.AddFirst(cacheKey);
                return newImage;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtworkPathConverter] {ex}");
            string fallbackUri = (parameter as string ?? "").Equals("Artist", StringComparison.OrdinalIgnoreCase) 
                ? "ms-appx:///Assets/PlaceholderArtist.png" 
                : "ms-appx:///Assets/PlaceholderAlbum.png";
            return new BitmapImage(new Uri(fallbackUri)) { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = 256 };
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
