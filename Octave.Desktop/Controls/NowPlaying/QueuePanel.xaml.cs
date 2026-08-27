using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Octave.Core.Models;
using Octave.Core.Services.Library;

namespace Octave_Desktop.Controls.NowPlaying;

public sealed partial class QueuePanel : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(QueuePanel), new PropertyMetadata(null, OnItemsSourceChanged));

    public event EventHandler<QueueItem>? PlayItemRequested;
    // UI-NP-02: per-row remove request.
    public event EventHandler<QueueItem>? RemoveItemRequested;
    public event RoutedEventHandler? ClearQueueRequested;

    private readonly List<Button> _favoriteButtons = new();
    private readonly HashSet<string> _favoriteTrackIds = new(StringComparer.Ordinal);
    private readonly ILibraryService? _libraryService;
    private readonly EventHandler _favoritesChangedHandler;

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public QueuePanel()
    {
        InitializeComponent();
        _libraryService = App.Services?.GetService<ILibraryService>();
        _favoritesChangedHandler = (s, e) => _ = RefreshFavoriteIdsAsync();
        if (_libraryService != null)
        {
            _libraryService.FavoritesChanged += _favoritesChangedHandler;
        }
        this.Loaded += (s, e) => _ = RefreshFavoriteIdsAsync();
        this.Unloaded += (s, e) =>
        {
            if (_libraryService != null)
            {
                _libraryService.FavoritesChanged -= _favoritesChangedHandler;
            }
        };
    }

    private async Task RefreshFavoriteIdsAsync()
    {
        if (_libraryService == null) return;
        try
        {
            var favIds = await _libraryService.GetFavoriteTrackIdsAsync();
            DispatcherQueue?.TryEnqueue(() =>
            {
                _favoriteTrackIds.Clear();
                foreach (var id in favIds) _favoriteTrackIds.Add(id);
                _favoriteButtons.RemoveAll(btn => btn.XamlRoot == null);
                foreach (var btn in _favoriteButtons)
                {
                    UpdateFavoriteButtonIcon(btn);
                }
            });
        }
        catch { }
    }

    private void ItemFavoriteButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && !_favoriteButtons.Contains(btn))
        {
            _favoriteButtons.Add(btn);
            UpdateFavoriteButtonIcon(btn);
        }
    }

    private void ItemFavoriteButton_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Button btn)
        {
            UpdateFavoriteButtonIcon(btn);
        }
    }

    private void UpdateFavoriteButtonIcon(Button btn)
    {
        if (btn.Content is FontIcon fontIcon && btn.DataContext is QueueItem item)
        {
            bool isFav = _favoriteTrackIds.Contains(item.Track.Id);
            fontIcon.Glyph = isFav ? "\uEB52" : "\uEB51";
            fontIcon.Foreground = isFav
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 64, 96))
                : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
            ToolTipService.SetToolTip(btn, isFav ? "Remove from Favorites" : "Add to Favorites");
        }
    }

    private async void ItemFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is QueueItem item && _libraryService != null)
        {
            bool newFav = await _libraryService.ToggleFavoriteAsync(item.Track.Id);
            if (newFav) _favoriteTrackIds.Add(item.Track.Id);
            else _favoriteTrackIds.Remove(item.Track.Id);
            UpdateFavoriteButtonIcon(btn);
        }
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is QueuePanel panel)
        {
            panel.QueueListView.ItemsSource = panel.ItemsSource;
            _ = panel.RefreshFavoriteIdsAsync();
        }
    }

    private void QueueListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QueueItem item)
        {
            PlayItemRequested?.Invoke(this, item);
        }
    }

    private void ItemPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is QueueItem item)
        {
            PlayItemRequested?.Invoke(this, item);
        }
    }

    private void ItemRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is QueueItem item)
        {
            RemoveItemRequested?.Invoke(this, item);
        }
    }

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e)
    {
        ClearQueueRequested?.Invoke(this, e);
    }
}
