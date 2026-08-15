using System;
using System.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Octave.Core.Models;

namespace Octave_Desktop.Controls.NowPlaying;

public sealed partial class QueuePanel : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(QueuePanel), new PropertyMetadata(null, OnItemsSourceChanged));

    public event EventHandler<QueueItem>? PlayItemRequested;
    public event RoutedEventHandler? ClearQueueRequested;

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public QueuePanel()
    {
        InitializeComponent();
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is QueuePanel panel)
        {
            panel.QueueListView.ItemsSource = panel.ItemsSource;
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

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e)
    {
        ClearQueueRequested?.Invoke(this, e);
    }
}
