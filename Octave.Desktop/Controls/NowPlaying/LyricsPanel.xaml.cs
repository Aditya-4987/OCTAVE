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

    // NF-25: auto-follow state. While the user is scrolled away from the active
    // line, highlighting keeps running but the panel stops repositioning itself;
    // the sync button appears to jump back and resume following.
    private bool _followEnabled = true;

    // True while a programmatic ChangeView animation is in flight, so its own
    // ViewChanged callbacks are not mistaken for user scrolling.
    private bool _autoScrollInFlight;

    private static void OnSyncedLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsPanel panel)
        {
            panel._lineElements.Clear();
            panel._lastHighlightedIndex = -2;
            // NF-25: a fresh lyric sheet starts following again.
            panel._followEnabled = true;
            panel._autoScrollInFlight = false;
            panel.SyncLyricsButton.Visibility = Visibility.Collapsed;
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
        // NF-25: the sync button exists only in the synced view - and only while
        // the user has scrolled away from the active line.
        SyncLyricsButton.Visibility = (State == LyricsState.Synced && !_followEnabled)
            ? Visibility.Visible
            : Visibility.Collapsed;
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

            // NF-25: highlight always runs; repositioning only while following.
            if (_followEnabled)
            {
                ScrollActiveLineIntoView();
            }
        }
    }

    // NP-16 successor: scrolls the ACTIVE line to ~40% of the viewport with
    // ChangeView on THIS panel's own viewer. StartBringIntoView was replaced
    // because it bubbles through EVERY ancestor ScrollViewer - each lyric tick
    // yanked the whole Now Playing page (NF-25). UpdateLayout() first so the
    // just-applied 18->22px font change is reflected in the offsets.
    private void ScrollActiveLineIntoView()
    {
        try
        {
            int idx = CurrentLyricIndex;
            if (idx < 0 || idx >= _lineElements.Count) return;
            var tb = _lineElements[idx];

            tb.UpdateLayout();

            double contentY = tb.TransformToVisual(SyncedItemsControl)
                                 .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            double target = contentY + SyncedScrollViewer.Padding.Top
                            - (SyncedScrollViewer.ViewportHeight * 0.4);
            target = Math.Clamp(target, 0, Math.Max(0, SyncedScrollViewer.ScrollableHeight));

            _autoScrollInFlight = true;
            SyncedScrollViewer.ChangeView(null, target, null, disableAnimation: false);
        }
        catch { }
    }

    // NF-25: any view movement that is not our own auto-scroll animation is the
    // user - suspend following and surface the sync button.
    private void SyncedScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_autoScrollInFlight)
        {
            if (!e.IsIntermediate) _autoScrollInFlight = false;
            return;
        }

        SuspendFollow();
    }

    // Mouse wheel over the lyrics suspends following immediately (ViewChanged
    // alone cannot distinguish a wheel tick from the tail of our own animation).
    private void SyncedScrollViewer_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        SuspendFollow();
    }

    private void SuspendFollow()
    {
        if (!_followEnabled || State != LyricsState.Synced) return;

        _followEnabled = false;
        SyncLyricsButton.Visibility = Visibility.Visible;
    }

    private void SyncLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        // Resume following and jump straight back to the active line - even when
        // the index itself has not moved since the user scrolled away.
        _followEnabled = true;
        SyncLyricsButton.Visibility = Visibility.Collapsed;
        _lastHighlightedIndex = -2; // force the scroll path to run again
        UpdateLyricHighlighting();
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

    // NF-26: undo all +/- nudges in one click - the delta that returns OffsetMs
    // to exactly zero, through the same pipeline the nudges use.
    private void ResetOffsetButton_Click(object sender, RoutedEventArgs e)
    {
        if (OffsetMs != 0)
        {
            OffsetChangeRequested?.Invoke(this, -OffsetMs);
        }
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
