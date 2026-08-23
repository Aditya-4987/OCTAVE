using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Octave.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

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

    // UI-NP-06: current sync offset (ms), pushed down by the view model so the
    // toolbar can show what is applied.
    public static readonly DependencyProperty OffsetMsProperty =
        DependencyProperty.Register(nameof(OffsetMs), typeof(int), typeof(LyricsPanel), new PropertyMetadata(0, OnOffsetChanged));

    // UI-NP-06: raised with the requested delta (+/-500 ms); the owning page
    // forwards it to the view model, which owns the actual timing math.
    public event EventHandler<int>? OffsetChangeRequested;

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

    public int OffsetMs
    {
        get => (int)GetValue(OffsetMsProperty);
        set => SetValue(OffsetMsProperty, value);
    }

    public LyricsPanel()
    {
        InitializeComponent();
        UpdateOffsetLabel();
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsPanel panel)
        {
            panel.UpdateStateViews();
        }
    }

    private static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsPanel panel)
        {
            panel.UpdateOffsetLabel();
        }
    }

    private int _lastHighlightedIndex = -2;

    private static void OnSyncedLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsPanel panel)
        {
            panel._lineElements.Clear();
            panel._lastHighlightedIndex = -2;
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

        // UI-NP-06: the nudge/copy toolbar only makes sense while lyrics are shown.
        bool hasLyrics = State == LyricsState.Synced || State == LyricsState.Unsynced;
        ToolbarRow.Visibility = hasLyrics ? Visibility.Visible : Visibility.Collapsed;
        if (hasLyrics)
        {
            UpdateOffsetLabel();
        }

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
            ApplyLineHighlight(tb, _lineElements.Count - 1 == CurrentLyricIndex);
        }
    }

    private void UpdateLyricHighlighting()
    {
        int targetIdx = CurrentLyricIndex;
        if (targetIdx == _lastHighlightedIndex) return;

        int previousIdx = _lastHighlightedIndex;
        _lastHighlightedIndex = targetIdx;

        // NP-15: touch only the outgoing and incoming lines - repainting every
        // element on each index change was O(N) property churn per lyric tick.
        if (previousIdx >= 0 && previousIdx < _lineElements.Count)
        {
            ApplyLineHighlight(_lineElements[previousIdx], active: false);
        }

        if (targetIdx >= 0 && targetIdx < _lineElements.Count && SyncedLines != null && targetIdx < SyncedLines.Count)
        {
            var tb = _lineElements[targetIdx];
            ApplyLineHighlight(tb, active: true);

            // NP-16 / UI-NP-03: exactly one scroll mechanism. The old code fired
            // ListView.ScrollIntoView (Leading) AND StartBringIntoView
            // (ratio 0.4) at the same time - two competing scroll targets caused
            // the visible stutter. StartBringIntoView alone animates smoothly.
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
    }

    private void ApplyLineHighlight(TextBlock tb, bool active)
    {
        if (active)
        {
            tb.Opacity = 1.0;
            tb.Foreground = ResolveAccentBrush();
            tb.FontSize = 22;
        }
        else
        {
            tb.Opacity = 0.35;
            tb.Foreground = ResolveDefaultBrush();  // NP-14: themed, not hardcoded White
            tb.FontSize = 18;
        }
    }

    private static Brush ResolveAccentBrush()
    {
        // Resolved per call, never cached: a brush cached across a light/dark
        // switch keeps the old theme's color forever (NP-14 family).
        if (Application.Current.Resources.TryGetValue("SystemControlHighlightAccentBrush", out object? value) && value is Brush accent)
        {
            return accent;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
    }

    private static Brush ResolveDefaultBrush()
    {
        if (Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out object? value) && value is Brush fill)
        {
            return fill;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private void UpdateOffsetLabel()
    {
        int ms = OffsetMs;
        // U+2212 (minus sign) renders cleaner than hyphen at this size.
        OffsetLabel.Text = ms == 0
            ? "LYRICS SYNC"
            : $"LYRICS SYNC {(ms > 0 ? "+" : "−")}{Math.Abs(ms) / 1000.0:0.#} s";
    }

    private void DecreaseOffsetButton_Click(object sender, RoutedEventArgs e)
    {
        OffsetChangeRequested?.Invoke(this, -500);
    }

    private void IncreaseOffsetButton_Click(object sender, RoutedEventArgs e)
    {
        OffsetChangeRequested?.Invoke(this, 500);
    }

    private void CopyLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? text;
            if (State == LyricsState.Synced && SyncedLines is { Count: > 0 })
            {
                text = string.Join(Environment.NewLine, SyncedLines.Select(l => l.Text));
            }
            else if (State == LyricsState.Unsynced && !string.IsNullOrEmpty(UnsyncedTextBlock.Text))
            {
                text = UnsyncedTextBlock.Text;
            }
            else
            {
                return;
            }

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricsPanel] Copy to clipboard failed: {ex.Message}");
        }
    }
}
