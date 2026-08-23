using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Octave_Desktop.Converters;

public class ArtworkPathConverter : IValueConverter
{
    private class CacheEntry
    {
        public required LinkedListNode<string> Node { get; init; }
        public required BitmapImage Image { get; init; }
    }

    private const int MaxCacheSize = 250;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> LruList = new();

    public static void ClearCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
            LruList.Clear();
        }
    }

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
                // NF-09 (Batch 10): the token→absolute-path mapping belongs to the
                // manager that owns the token layout — this used to re-implement
                // "<LocalFolder>\ArtworkCache" here and would break silently if
                // the cache root ever moved.
                // WinUI's BitmapImage decodes ms-appdata:///local/ asynchronously on a background worker thread
                uriString = App.Services.GetRequiredService<Octave.Core.Interfaces.IArtworkCacheManager>()
                    .ResolveTokenPath(artworkUrl);
            }
            else if (artworkUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) || 
                     artworkUrl.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase) ||
                     artworkUrl.StartsWith("ms-appdata", StringComparison.OrdinalIgnoreCase))
            {
                uriString = artworkUrl;
            }
            else
            {
                uriString = isArtist ? "ms-appx:///Assets/PlaceholderArtist.png" : "ms-appx:///Assets/PlaceholderAlbum.png";
            }

            int targetLogicalWidth = 320;
            if (!string.IsNullOrWhiteSpace(parameterString))
            {
                if (parameterString.Equals("Small", StringComparison.OrdinalIgnoreCase) || 
                    parameterString.Equals("Thumb", StringComparison.OrdinalIgnoreCase) ||
                    parameterString.Equals("56", StringComparison.OrdinalIgnoreCase) ||
                    parameterString.Equals("40", StringComparison.OrdinalIgnoreCase))
                {
                    targetLogicalWidth = 112;
                }
                else if (parameterString.Equals("Artist", StringComparison.OrdinalIgnoreCase) ||
                         parameterString.Equals("Card", StringComparison.OrdinalIgnoreCase) ||
                         parameterString.Equals("Medium", StringComparison.OrdinalIgnoreCase) ||
                         parameterString.Equals("160", StringComparison.OrdinalIgnoreCase))
                {
                    targetLogicalWidth = 320;
                }
                else if (parameterString.Equals("Large", StringComparison.OrdinalIgnoreCase) ||
                         parameterString.Equals("NowPlaying", StringComparison.OrdinalIgnoreCase) ||
                         parameterString.Equals("Background", StringComparison.OrdinalIgnoreCase) ||
                         parameterString.Equals("500", StringComparison.OrdinalIgnoreCase))
                {
                    targetLogicalWidth = 600;
                }
                else if (int.TryParse(parameterString, out int customWidth) && customWidth > 0)
                {
                    targetLogicalWidth = customWidth;
                }
            }

            double scale = 1.0;
            try
            {
                if (App.MainWindowInstance?.Content?.XamlRoot != null)
                {
                    scale = App.MainWindowInstance.Content.XamlRoot.RasterizationScale;
                }
            }
            catch { }
            if (scale <= 0) scale = 1.0;

            int targetPhysicalWidth = (int)Math.Round(targetLogicalWidth * scale);
            if (targetPhysicalWidth <= 0) targetPhysicalWidth = targetLogicalWidth;

            string cacheKey = $"{uriString}_{targetPhysicalWidth}_{scale:F2}";

            lock (CacheLock)
            {
                if (Cache.TryGetValue(cacheKey, out var entry))
                {
                    LruList.Remove(entry.Node);
                    LruList.AddFirst(entry.Node);
                    return entry.Image;
                }

                var newImage = new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Physical,
                    DecodePixelWidth = targetPhysicalWidth,
                    UriSource = new Uri(uriString)
                };

                if (Cache.Count >= MaxCacheSize && LruList.Last != null)
                {
                    string oldestKey = LruList.Last.Value;
                    LruList.RemoveLast();
                    Cache.Remove(oldestKey);
                }

                var node = new LinkedListNode<string>(cacheKey);
                LruList.AddFirst(node);
                Cache[cacheKey] = new CacheEntry { Node = node, Image = newImage };
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
