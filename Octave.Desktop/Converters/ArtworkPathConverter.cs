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

    private static readonly BitmapImage PlaceholderArtist = new(new Uri("ms-appx:///Assets/PlaceholderArtist.png"))
    {
        DecodePixelType = DecodePixelType.Logical,
        DecodePixelWidth = 512
    };
    private static readonly BitmapImage PlaceholderAlbum = new(new Uri("ms-appx:///Assets/PlaceholderAlbum.png"))
    {
        DecodePixelType = DecodePixelType.Logical,
        DecodePixelWidth = 512
    };

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        try
        {
            string? artworkUrl = value as string;
            string parameterString = parameter as string ?? string.Empty;
            bool isArtist = parameterString.Equals("Artist", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(artworkUrl))
            {
                return isArtist ? PlaceholderArtist : PlaceholderAlbum;
            }

            string uriString;
            if (artworkUrl.StartsWith("ArtworkCache/", StringComparison.OrdinalIgnoreCase))
            {
                string absolutePath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, artworkUrl.Replace('/', '\\'));
                if (File.Exists(absolutePath))
                {
                    uriString = absolutePath;
                }
                else
                {
                    return isArtist ? PlaceholderArtist : PlaceholderAlbum;
                }
            }
            else if (artworkUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) || 
                     artworkUrl.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase))
            {
                uriString = artworkUrl;
            }
            else
            {
                return isArtist ? PlaceholderArtist : PlaceholderAlbum;
            }

            int targetDecodeWidth = 256;
            if (parameter is string paramStr && !string.IsNullOrWhiteSpace(paramStr))
            {
                if (paramStr.Equals("Small", StringComparison.OrdinalIgnoreCase) || 
                    paramStr.Equals("Thumb", StringComparison.OrdinalIgnoreCase) ||
                    paramStr.Equals("56", StringComparison.OrdinalIgnoreCase) ||
                    paramStr.Equals("40", StringComparison.OrdinalIgnoreCase))
                {
                    targetDecodeWidth = 128;
                }
                else if (paramStr.Equals("Artist", StringComparison.OrdinalIgnoreCase) ||
                         paramStr.Equals("Card", StringComparison.OrdinalIgnoreCase) ||
                         paramStr.Equals("Medium", StringComparison.OrdinalIgnoreCase) ||
                         paramStr.Equals("160", StringComparison.OrdinalIgnoreCase))
                {
                    targetDecodeWidth = 256;
                }
                else if (paramStr.Equals("Large", StringComparison.OrdinalIgnoreCase) ||
                         paramStr.Equals("NowPlaying", StringComparison.OrdinalIgnoreCase) ||
                         paramStr.Equals("Background", StringComparison.OrdinalIgnoreCase) ||
                         paramStr.Equals("500", StringComparison.OrdinalIgnoreCase))
                {
                    targetDecodeWidth = 640;
                }
                else if (int.TryParse(paramStr, out int customWidth) && customWidth > 0)
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
            return (parameter as string ?? "").Equals("Artist", StringComparison.OrdinalIgnoreCase) ? PlaceholderArtist : PlaceholderAlbum;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
