using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Octave_Desktop.Converters;

public class ArtworkPathConverter : IValueConverter
{
    private class CacheEntry
    {
        public required LinkedListNode<string> Node { get; init; }
        public required BitmapImage Image { get; init; }
    }

    // NF-28: what (artwork url, converter parameter) produced each decoded
    // BitmapImage, so a display-scale change can re-decode every bound image.
    // A ConditionalWeakTable needs no eviction management - entries die with
    // their images, including LRU evictions. (TValue must be a reference type.)
    private sealed class SourceContext
    {
        public SourceContext(string? uri, string parameter) { Uri = uri; Parameter = parameter; }
        public string? Uri { get; }
        public string Parameter { get; }
    }

    private static readonly ConditionalWeakTable<BitmapImage, SourceContext> SourceContexts = new();

    private const int MaxCacheSize = 250;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> LruList = new();

    // NF-29: windows that declare their own converter instance can point it at
    // their own XamlRoot. Without this, the mini player on a monitor with a
    // different DPI decoded at the MAIN window's scale and its artwork was
    // mis-sized for its own display.
    public Func<double>? ScaleProvider { get; set; }

    public static void ClearCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
            LruList.Clear();
        }
        // SourceContexts cleans itself up with its images.
    }

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        string parameterString = parameter as string ?? string.Empty;
        try
        {
            string? artworkUrl = value as string;
            return GetImageAtScale(artworkUrl, parameterString, ResolveScale(ScaleProvider));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtworkPathConverter] {ex}");
            return GetImageAtScale(null, parameterString, ResolveScale(ScaleProvider));
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }

    private static double ResolveScale(Func<double>? provider)
    {
        try
        {
            if (provider != null)
            {
                double provided = provider();
                if (provided > 0) return provided;
            }
            if (App.MainWindowInstance?.Content?.XamlRoot != null)
            {
                return App.MainWindowInstance.Content.XamlRoot.RasterizationScale;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtworkPathConverter] ResolveScale failed: {ex.Message}");
        }
        return 1.0;
    }

    private static BitmapImage GetImageAtScale(string? artworkUrl, string parameterString, double scale)
    {
        if (scale <= 0) scale = 1.0;

        string uriString = ResolveUri(artworkUrl, parameterString);

        int targetLogicalWidth = 320;
        bool isBackdrop = false;
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
                     parameterString.Equals("500", StringComparison.OrdinalIgnoreCase))
            {
                targetLogicalWidth = 600;
            }
            // NF-30: the ambient backdrop stretches over the WHOLE window - the
            // 600-logical "Large" decode used to be GPU-upscaled ~4x across a
            // maximized window (soft, banded edges). Its own generous bucket,
            // capped in physical pixels so high-scale monitors cannot balloon
            // memory.
            else if (parameterString.Equals("Background", StringComparison.OrdinalIgnoreCase))
            {
                targetLogicalWidth = 960;
                isBackdrop = true;
            }
            else if (int.TryParse(parameterString, out int customWidth) && customWidth > 0)
            {
                targetLogicalWidth = customWidth;
            }
        }

        int physicalCap = isBackdrop ? 1920 : int.MaxValue;
        int targetPhysicalWidth = (int)Math.Min(Math.Round(targetLogicalWidth * scale), physicalCap);
        if (targetPhysicalWidth <= 0) targetPhysicalWidth = targetLogicalWidth;

        // Key by the effective decode width: two scales that clamp to the same
        // width share one bitmap instead of decoding duplicates.
        string cacheKey = $"{uriString}_{targetPhysicalWidth}";

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
            SourceContexts.AddOrUpdate(newImage, new SourceContext(artworkUrl, parameterString));

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

    private static string ResolveUri(string? artworkUrl, string parameterString)
    {
        if (string.IsNullOrWhiteSpace(artworkUrl))
        {
            return IsArtistParameter(parameterString)
                ? "ms-appx:///Assets/PlaceholderArtist.png"
                : "ms-appx:///Assets/PlaceholderAlbum.png";
        }

        if (artworkUrl.StartsWith("ArtworkCache/", StringComparison.OrdinalIgnoreCase))
        {
            // NF-09 (Batch 10): the token→absolute-path mapping belongs to the
            // manager that owns the token layout — this used to re-implement
            // "<LocalFolder>\ArtworkCache" here and would break silently if
            // the cache root ever moved.
            // WinUI's BitmapImage decodes ms-appdata:///local/ asynchronously on a background worker thread
            return App.Services.GetRequiredService<Octave.Core.Interfaces.IArtworkCacheManager>()
                .ResolveTokenPath(artworkUrl);
        }

        if (artworkUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            artworkUrl.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase) ||
            artworkUrl.StartsWith("ms-appdata", StringComparison.OrdinalIgnoreCase))
        {
            return artworkUrl;
        }

        return IsArtistParameter(parameterString)
            ? "ms-appx:///Assets/PlaceholderArtist.png"
            : "ms-appx:///Assets/PlaceholderAlbum.png";
    }

    private static bool IsArtistParameter(string parameterString) =>
        parameterString.Equals("Artist", StringComparison.OrdinalIgnoreCase);

    // ==================================================================
    // NF-28: display-scale change handling.
    //
    // Decoded bitmaps are cached per scale; nothing used to react when the
    // user dragged the window onto a monitor with a different DPI, so every
    // artwork stayed bound to its old-density decode (soft or pixelated)
    // until some navigation happened to re-run its binding.
    // AttachDisplayScaleMonitor watches the window's XamlRoot; on a real
    // scale change it drops the stale decodes and walks the visual tree,
    // re-decoding every image this converter produced (Images AND ImageBrush
    // fills) at the NEW scale.
    // ==================================================================

    // Per-scope monitor state that cannot pin closed windows - entries die with
    // their framework elements. Tracks the wired XamlRoot (so a re-Loaded scope
    // cannot stack duplicate root.Changed handlers) and the scale we last
    // refreshed at (shared by every trigger path).
    private sealed class ScaleMonitorState
    {
        public bool LoadedHooked;
        public Microsoft.UI.Xaml.XamlRoot? WiredRoot;
        public double LastScale;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, ScaleMonitorState> MonitoredScopes = new();

    public static void AttachDisplayScaleMonitor(FrameworkElement scope)
    {
        var state = MonitoredScopes.GetOrCreateValue(scope);
        if (!state.LoadedHooked)
        {
            state.LoadedHooked = true;
            scope.Loaded += Scope_Loaded;
        }

        // Already-loaded scopes (attached after layout) wire immediately.
        if (scope.XamlRoot != null)
        {
            WireRootChanged(scope, state);
        }
    }

    private static void Scope_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement scope && MonitoredScopes.TryGetValue(scope, out var state))
        {
            WireRootChanged(scope, state);
        }
    }

    private static void WireRootChanged(FrameworkElement scope, ScaleMonitorState state)
    {
        var root = scope.XamlRoot;
        if (root == null || ReferenceEquals(root, state.WiredRoot)) return;

        state.WiredRoot = root;
        state.LastScale = root.RasterizationScale;

        root.Changed += (s, e) =>
        {
            // An exception escaping a WinRT-raised event surfaces as a stowed
            // exception (process exit 0xC000027B), so the whole walk is guarded.
            try
            {
                EvaluateScopeScale(scope);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ArtworkPathConverter] Scale-change refresh failed: {ex.Message}");
            }
        };
    }

    /// <summary>
    /// NF-32: deterministic companion to XamlRoot.Changed - call when a window
    /// reports it MOVED (AppWindow.Changed / DidPositionChange). Crossing a
    /// monitor boundary always raises it, while the XamlRoot signal proved
    /// unreliable on its own; evaluating the live RasterizationScale here makes
    /// the drag-across-monitors refresh unconditional.
    /// </summary>
    public static void CheckDisplayScale(FrameworkElement scope)
    {
        try
        {
            var root = scope.XamlRoot;
            if (root == null) return;

            var state = MonitoredScopes.GetOrCreateValue(scope);
            if (state.WiredRoot == null)
            {
                // Never attached: seed the baseline, refresh nothing.
                state.WiredRoot = root;
                state.LastScale = root.RasterizationScale;
                return;
            }

            EvaluateScopeScale(scope);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtworkPathConverter] Scale check failed: {ex.Message}");
        }
    }

    // XamlRootChangedEventArgs carries no values - read the live scale off the
    // captured root and compare against the shared baseline.
    private static void EvaluateScopeScale(FrameworkElement scope)
    {
        var root = scope.XamlRoot;
        if (root == null) return;

        var state = MonitoredScopes.GetOrCreateValue(scope);
        double newScale = root.RasterizationScale;
        if (Math.Abs(newScale - state.LastScale) < 0.01) return;

        state.LastScale = newScale;

        HandleDisplayScaleChanged();

        // Refresh from the window content down so EVERY visible surface
        // re-decodes, not just the subtree the scope anchors.
        var refreshRoot = scope;
        while (refreshRoot.Parent is FrameworkElement parent)
        {
            refreshRoot = parent;
        }
        RefreshBoundArtwork(refreshRoot, newScale);
    }

    public static void HandleDisplayScaleChanged()
    {
        ClearCache();
    }

    /// <summary>
    /// Re-runs the converter for every Image / ImageBrush fill under <paramref name="root"/>
    /// whose current source came from this converter, replacing sources decoded for an
    /// older display scale. Bound Images are refreshed by RE-APPLYING their binding (which
    /// re-runs the converter at the new scale and keeps the binding live for later source
    /// changes); only unbound sources are swapped directly. Images NOT decoded by this
    /// converter are left alone.
    /// </summary>
    public static void RefreshBoundArtwork(UIElement root, double newScale)
    {
        WalkAndRefresh(root, newScale);
    }

    private static void WalkAndRefresh(DependencyObject node, double newScale)
    {
        // The walk runs while the app keeps using the UI: elements can be
        // disconnected mid-iteration and property reads can throw. Guard each
        // step - a single bad node must not abort the remaining refreshes.
        try
        {
            if (node is Image image &&
                image.Source is BitmapImage imageBitmap &&
                SourceContexts.TryGetValue(imageBitmap, out var imageCtx))
            {
                // NF-34: most artwork Images are driven by a OneWay {Binding} (the
                // transport-bar CurrentArtworkUrl, the ambient backdrop, list
                // thumbnails). Assigning image.Source sets a LOCAL value, which
                // REMOVES that binding - so after the first cross-monitor refresh
                // the image froze and never updated again when the track changed.
                // Re-applying the live binding re-runs the converter at the new
                // scale WITHOUT detaching it; only truly source-assigned images
                // (no binding expression) fall back to a direct swap.
                var expr = image.GetBindingExpression(Image.SourceProperty);
                if (expr?.ParentBinding != null)
                {
                    image.SetBinding(Image.SourceProperty, expr.ParentBinding);
                }
                else
                {
                    var fresh = GetImageAtScale(imageCtx.Uri, imageCtx.Parameter, newScale);
                    if (!ReferenceEquals(fresh, imageBitmap))
                    {
                        image.Source = fresh;
                    }
                }
            }

            if (node is Shape shape &&
                shape.Fill is ImageBrush shapeBrush &&
                shapeBrush.ImageSource is BitmapImage shapeBitmap &&
                SourceContexts.TryGetValue(shapeBitmap, out var shapeCtx))
            {
                var freshFill = GetImageAtScale(shapeCtx.Uri, shapeCtx.Parameter, newScale);
                if (!ReferenceEquals(freshFill, shapeBitmap))
                {
                    shapeBrush.ImageSource = freshFill;
                }
            }

            if (node is Border border &&
                border.Background is ImageBrush borderBrush &&
                borderBrush.ImageSource is BitmapImage borderBitmap &&
                SourceContexts.TryGetValue(borderBitmap, out var borderCtx))
            {
                var freshBackground = GetImageAtScale(borderCtx.Uri, borderCtx.Parameter, newScale);
                if (!ReferenceEquals(freshBackground, borderBitmap))
                {
                    borderBrush.ImageSource = freshBackground;
                }
            }

            if (node is Panel panel &&
                panel.Background is ImageBrush panelBrush &&
                panelBrush.ImageSource is BitmapImage panelBitmap &&
                SourceContexts.TryGetValue(panelBitmap, out var panelCtx))
            {
                var freshPanelFill = GetImageAtScale(panelCtx.Uri, panelCtx.Parameter, newScale);
                if (!ReferenceEquals(freshPanelFill, panelBitmap))
                {
                    panelBrush.ImageSource = freshPanelFill;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtworkPathConverter] Node refresh skipped ({node.GetType().Name}): {ex.Message}");
        }

        int count;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(node);
        }
        catch
        {
            return; // node left the tree mid-walk - its subtree is gone anyway
        }

        for (int i = 0; i < count; i++)
        {
            DependencyObject child;
            try
            {
                child = VisualTreeHelper.GetChild(node, i);
            }
            catch
            {
                continue; // children shifted under us - skip this slot
            }
            WalkAndRefresh(child, newScale);
        }
    }
}
