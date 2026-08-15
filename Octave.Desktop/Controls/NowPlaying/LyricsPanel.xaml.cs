using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Octave.Core.Models;
using System.Collections.Generic;

namespace Octave_Desktop.Controls.NowPlaying;

public sealed partial class LyricsPanel : UserControl
{
    private readonly List<TextBlock> _lineElements = new();

    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(LyricsState), typeof(LyricsPanel), new PropertyMetadata(LyricsState.Loading, OnStateChanged));

    public static readonly DependencyProperty SyncedLinesProperty =
        DependencyProperty.Register(nameof(SyncedLines), typeof(IReadOnlyList<LyricLine>), typeof(LyricsPanel), new PropertyMetadata(null, OnSyncedLinesChanged));

    public static readonly DependencyProperty UnsyncedTextProperty =
        DependencyProperty.Register(nameof(UnsyncedText), typeof(string), typeof(LyricsPanel), new PropertyMetadata(null, OnUnsyncedTextChanged));

    public static readonly DependencyProperty CurrentLyricIndexProperty =
        DependencyProperty.Register(nameof(CurrentLyricIndex), typeof(int), typeof(LyricsPanel), new PropertyMetadata(-1, OnCurrentIndexChanged));

    public LyricsState State
    {
        get => (LyricsState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public IReadOnlyList<LyricLine>? SyncedLines
    {
        get => (IReadOnlyList<LyricLine>?)GetValue(SyncedLinesProperty);
        set => SetValue(SyncedLinesProperty, value);
    }

    public string? UnsyncedText
    {
        get => (string?)GetValue(UnsyncedTextProperty);
        set => SetValue(UnsyncedTextProperty, value);
    }

    public int CurrentLyricIndex
    {
        get => (int)GetValue(CurrentLyricIndexProperty);
        set => SetValue(CurrentLyricIndexProperty, value);
    }

    public LyricsPanel()
    {
        InitializeComponent();
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsPanel panel)
        {
            panel.UpdateStateViews();
        }
    }

    private static void OnSyncedLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsPanel panel)
        {
            panel._lineElements.Clear();
            panel.SyncedItemsControl.ItemsSource = panel.SyncedLines;
            panel.UpdateStateViews();
        }
    }

    private static void OnUnsyncedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsPanel panel)
        {
            panel.UnsyncedTextBlock.Text = panel.UnsyncedText ?? "";
            panel.UpdateStateViews();
        }
    }

    private static void OnCurrentIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsPanel panel)
        {
            panel.UpdateLyricHighlighting();
        }
    }

    private void UpdateStateViews()
    {
        LoadingContainer.Visibility = State == LyricsState.Loading ? Visibility.Visible : Visibility.Collapsed;
        UnavailableContainer.Visibility = State == LyricsState.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        UnsyncedScrollViewer.Visibility = State == LyricsState.Unsynced ? Visibility.Visible : Visibility.Collapsed;
        SyncedScrollViewer.Visibility = State == LyricsState.Synced ? Visibility.Visible : Visibility.Collapsed;

        if (State == LyricsState.Synced)
        {
            UpdateLyricHighlighting();
        }
    }

    private void LyricLineTextBlock_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb && !_lineElements.Contains(tb))
        {
            _lineElements.Add(tb);
            UpdateSingleLineHighlight(tb, _lineElements.Count - 1);
        }
    }

    private void UpdateLyricHighlighting()
    {
        int targetIdx = CurrentLyricIndex;
        Brush activeBrush = (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"];
        Brush defaultBrush = new SolidColorBrush(Microsoft.UI.Colors.White);

        for (int i = 0; i < _lineElements.Count; i++)
        {
            var tb = _lineElements[i];
            if (i == targetIdx)
            {
                tb.Opacity = 1.0;
                tb.Foreground = activeBrush;
                tb.FontSize = 22;

                // Scroll active line into view smoothly
                try
                {
                    tb.StartBringIntoView(new BringIntoViewOptions
                    {
                        AnimationDesired = true,
                        VerticalAlignmentRatio = 0.4
                    });
                }
                catch { }
            }
            else
            {
                tb.Opacity = 0.35;
                tb.Foreground = defaultBrush;
                tb.FontSize = 18;
            }
        }
    }

    private void UpdateSingleLineHighlight(TextBlock tb, int index)
    {
        if (index == CurrentLyricIndex)
        {
            tb.Opacity = 1.0;
            tb.Foreground = (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"];
            tb.FontSize = 22;
        }
        else
        {
            tb.Opacity = 0.35;
            tb.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            tb.FontSize = 18;
        }
    }
}
