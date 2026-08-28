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
    private bool _suppressSelectionChanged;

    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(LyricsState), typeof(LyricsPanel), new PropertyMetadata(LyricsState.Loading, OnStateChanged));

    public static readonly DependencyProperty SyncedLinesProperty =
        DependencyProperty.Register(nameof(SyncedLines), typeof(IReadOnlyList<LyricLine>), typeof(LyricsPanel), new PropertyMetadata(null, OnSyncedLinesChanged));

    public static readonly DependencyProperty UnsyncedTextProperty =
        DependencyProperty.Register(nameof(UnsyncedText), typeof(string), typeof(LyricsPanel), new PropertyMetadata(null, OnUnsyncedTextChanged));

    public static readonly DependencyProperty CurrentLyricIndexProperty =
        DependencyProperty.Register(nameof(CurrentLyricIndex), typeof(int), typeof(LyricsPanel), new PropertyMetadata(-1, OnCurrentIndexChanged));

    public static readonly DependencyProperty OffsetMsProperty =
        DependencyProperty.Register(nameof(OffsetMs), typeof(int), typeof(LyricsPanel), new PropertyMetadata(0, OnOffsetChanged));

    public static readonly DependencyProperty SelectedModeProperty =
        DependencyProperty.Register(nameof(SelectedMode), typeof(LyricDisplayMode), typeof(LyricsPanel), new PropertyMetadata(LyricDisplayMode.Synced, OnSelectedModeChanged));

    public static readonly DependencyProperty HasSyncedLyricsProperty =
        DependencyProperty.Register(nameof(HasSyncedLyrics), typeof(bool), typeof(LyricsPanel), new PropertyMetadata(false, OnAvailabilityChanged));

    public static readonly DependencyProperty HasPlainLyricsProperty =
        DependencyProperty.Register(nameof(HasPlainLyrics), typeof(bool), typeof(LyricsPanel), new PropertyMetadata(false, OnAvailabilityChanged));

    public static readonly DependencyProperty LyricsSourceProperty =
        DependencyProperty.Register(nameof(LyricsSource), typeof(string), typeof(LyricsPanel), new PropertyMetadata(null, OnLyricsSourceChanged));

    public static readonly DependencyProperty ReloadLyricsCommandProperty =
        DependencyProperty.Register(nameof(ReloadLyricsCommand), typeof(System.Windows.Input.ICommand), typeof(LyricsPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty IsReloadingProperty =
        DependencyProperty.Register(nameof(IsReloading), typeof(bool), typeof(LyricsPanel), new PropertyMetadata(false, OnIsReloadingChanged));

    public event EventHandler<int>? OffsetChangeRequested;
    public event EventHandler<LyricDisplayMode>? LyricModeChangeRequested;
    public event EventHandler? ReloadRequested;

    public System.Windows.Input.ICommand? ReloadLyricsCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(ReloadLyricsCommandProperty);
        set => SetValue(ReloadLyricsCommandProperty, value);
    }

    public bool IsReloading
    {
        get => (bool)GetValue(IsReloadingProperty);
        set => SetValue(IsReloadingProperty, value);
    }

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

    public string? LyricsSource
    {
        get => (string?)GetValue(LyricsSourceProperty);
        set => SetValue(LyricsSourceProperty, value);
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

    public LyricDisplayMode SelectedMode
    {
        get => (LyricDisplayMode)GetValue(SelectedModeProperty);
        set => SetValue(SelectedModeProperty, value);
    }

    public bool HasSyncedLyrics
    {
        get => (bool)GetValue(HasSyncedLyricsProperty);
        set => SetValue(HasSyncedLyricsProperty, value);
    }

    public bool HasPlainLyrics
    {
        get => (bool)GetValue(HasPlainLyricsProperty);
        set => SetValue(HasPlainLyricsProperty, value);
    }

    public LyricsPanel()
    {
        InitializeComponent();
        UpdateDropdownItems();
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

    private static void OnSelectedModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsPanel panel)
        {
            panel.UpdateDropdownItems();
            panel.UpdateStateViews();
        }
    }

    private static void OnAvailabilityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsPanel panel)
        {
            panel.UpdateDropdownItems();
        }
    }

    private static void OnLyricsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsPanel panel)
        {
            panel.UpdateSourceComment();
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

    private void UpdateDropdownItems()
    {
        if (SyncedModeItem == null || StaticModeItem == null || LyricModeComboBox == null) return;

        SyncedModeItem.Content = HasSyncedLyrics ? "Synced Lyrics" : "Synced Lyrics (Unavailable)";
        SyncedModeItem.IsEnabled = HasSyncedLyrics;

        StaticModeItem.Content = HasPlainLyrics ? "Static Lyrics" : "Static Lyrics (Unavailable)";
        StaticModeItem.IsEnabled = HasPlainLyrics;

        _suppressSelectionChanged = true;
        LyricModeComboBox.SelectedIndex = SelectedMode == LyricDisplayMode.Synced ? 0 : 1;
        _suppressSelectionChanged = false;
    }

    private void LyricModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;

        var targetMode = LyricModeComboBox.SelectedIndex == 0 ? LyricDisplayMode.Synced : LyricDisplayMode.Static;

        if (targetMode == LyricDisplayMode.Synced && !HasSyncedLyrics)
        {
            _suppressSelectionChanged = true;
            LyricModeComboBox.SelectedIndex = SelectedMode == LyricDisplayMode.Synced ? 0 : 1;
            _suppressSelectionChanged = false;
            return;
        }

        if (targetMode == LyricDisplayMode.Static && !HasPlainLyrics)
        {
            _suppressSelectionChanged = true;
            LyricModeComboBox.SelectedIndex = SelectedMode == LyricDisplayMode.Synced ? 0 : 1;
            _suppressSelectionChanged = false;
            return;
        }

        LyricModeChangeRequested?.Invoke(this, targetMode);
    }

    private static void OnIsReloadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsPanel panel)
        {
            panel.UpdateStateViews();
        }
    }

    private void ReloadLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReloadLyricsCommand?.CanExecute(null) == true)
        {
            ReloadLyricsCommand.Execute(null);
        }
        ReloadRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateStateViews()
    {
        bool isLoading = State == LyricsState.Loading || State == LyricsState.Resolving;
        LoadingContainer.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        UnavailableContainer.Visibility = State == LyricsState.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        NetworkUnavailableContainer.Visibility = State == LyricsState.NetworkUnavailable ? Visibility.Visible : Visibility.Collapsed;
        UnsyncedScrollViewer.Visibility = State == LyricsState.Unsynced ? Visibility.Visible : Visibility.Collapsed;
        SyncedScrollViewer.Visibility = State == LyricsState.Synced ? Visibility.Visible : Visibility.Collapsed;

        // Toolbar: visible when lyrics exist
        bool hasLyrics = State == LyricsState.Synced || State == LyricsState.Unsynced;
        ToolbarRow.Visibility = hasLyrics ? Visibility.Visible : Visibility.Collapsed;

        // Sync controls group only visible in Synced Lyrics mode
        SyncControlsPanel.Visibility = (State == LyricsState.Synced) ? Visibility.Visible : Visibility.Collapsed;
        LyricModeComboBox.Visibility = hasLyrics ? Visibility.Visible : Visibility.Collapsed;

        // NF-25: the sync button exists only in the synced view - and only while
        // the user has scrolled away from the active line.
        SyncLyricsButton.Visibility = (State == LyricsState.Synced && !_followEnabled)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (hasLyrics)
        {
            UpdateOffsetLabel();
            UpdateDropdownItems();
            UpdateSourceComment();
        }
        else
        {
            UpdateSourceComment();
        }

        if (State == LyricsState.Synced)
        {
            UpdateLyricHighlighting();
        }
    }

    private void UpdateSourceComment()
    {
        string? comment = !string.IsNullOrWhiteSpace(LyricsSource)
            ? (LyricsSource.Equals("LRCLIB", StringComparison.OrdinalIgnoreCase)
                ? "Lyrics provided by LRCLIB"
                : $"Lyrics source: {LyricsSource}")
            : null;

        if (SyncedSourceCommentTextBlock != null)
        {
            SyncedSourceCommentTextBlock.Text = comment ?? "";
            SyncedSourceCommentTextBlock.Visibility = (State == LyricsState.Synced && !string.IsNullOrEmpty(comment))
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (UnsyncedSourceCommentTextBlock != null)
        {
            UnsyncedSourceCommentTextBlock.Text = comment ?? "";
            UnsyncedSourceCommentTextBlock.Visibility = (State == LyricsState.Unsynced && !string.IsNullOrEmpty(comment))
                ? Visibility.Visible
                : Visibility.Collapsed;
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

            if (!tb.IsLoaded || !SyncedItemsControl.IsLoaded || SyncedScrollViewer == null) return;

            tb.UpdateLayout();

            double contentY = tb.TransformToVisual(SyncedItemsControl)
                                 .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            double target = contentY + SyncedScrollViewer.Padding.Top
                            - (SyncedScrollViewer.ViewportHeight * 0.4);
            target = Math.Clamp(target, 0, Math.Max(0, SyncedScrollViewer.ScrollableHeight));

            _autoScrollInFlight = true;
            SyncedScrollViewer.ChangeView(null, target, null, disableAnimation: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricsPanel] ScrollActiveLineIntoView suppressed: {ex.Message}");
        }
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
            if (Math.Abs(tb.Opacity - 1.0) > 0.01) tb.Opacity = 1.0;
            tb.Foreground = ResolveAccentBrush();
            if (Math.Abs(tb.FontSize - 22) > 0.1) tb.FontSize = 22;
        }
        else
        {
            if (Math.Abs(tb.Opacity - 0.35) > 0.01) tb.Opacity = 0.35;
            tb.Foreground = ResolveDefaultBrush();
            if (Math.Abs(tb.FontSize - 18) > 0.1) tb.FontSize = 18;
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
        if (OffsetIndicator == null) return;

        if (ms != 0 && State == LyricsState.Synced)
        {
            OffsetIndicator.Text = $"{(ms > 0 ? "+" : "−")}{Math.Abs(ms) / 1000.0:0.#} s";
            OffsetIndicator.Visibility = Visibility.Visible;
            if (ResetOffsetButton != null) ResetOffsetButton.Visibility = Visibility.Visible;
        }
        else
        {
            OffsetIndicator.Visibility = Visibility.Collapsed;
            if (ResetOffsetButton != null) ResetOffsetButton.Visibility = Visibility.Collapsed;
        }
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
            if (ResetOffsetButton != null) ResetOffsetButton.Visibility = Visibility.Collapsed;
            if (OffsetIndicator != null) OffsetIndicator.Visibility = Visibility.Collapsed;
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
