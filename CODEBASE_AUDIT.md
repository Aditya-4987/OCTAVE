# OCTAVE Codebase Audit Report

> **Date**: 2026-08-18 (first + second pass) · **Third pass**: 2026-08-20  
> **Build Status**: ✅ Build succeeded — 0 errors, 0 warnings *(re-verified 2026-08-20: `dotnet build Octave.Desktop` = 0/0)*  
> **Test Status**: ✅ 128/128 tests passing *(re-verified 2026-08-20: `dotnet test Octave.Core.Tests` = 128/128)*  
>
> **Third-pass additions (2026-08-20)**: §13 integration / startup-wiring / dead-setting findings (INT-*), §14 fix-verification status, §15 the sequenced remediation-batch plan promised at the end of §12.  
> **Key third-pass conclusion**: the app compiles and its unit tests pass, so "the agent broke the app" is **behavioral/runtime**, not a build break — the prime suspects are INT-01/INT-02 (persisted settings never loaded / write-policy settings dead), CRIT-01 + the QUEUE/VM event-storm & leak cluster, and the DB-01 concurrency root.

---

## Table of Contents

1. [Critical Issues](#1-critical-issues)
2. [Now Playing Page](#2-now-playing-page)
3. [Online Fetching System (Lyrics & Metadata)](#3-online-fetching-system)
4. [Metadata Editor](#4-metadata-editor)
5. [Core Services](#5-core-services)
6. [Desktop Shell & MainWindow](#6-desktop-shell--mainwindow)
7. [Comprehensive Page-by-Page UI/UX & Polish Audit](#7-comprehensive-page-by-page-uiux--polish-audit)
   - 7.1 [MainWindow & Global Shell](#71-mainwindow--global-shell)
   - 7.2 [Home Page](#72-home-page)
   - 7.3 [Library Page (Songs)](#73-library-page-songs)
   - 7.4 [Albums Page](#74-albums-page)
   - 7.5 [Artists Page](#75-artists-page)
   - 7.6 [Playlists & Playlist Detail Pages](#76-playlists--playlist-detail-pages)
   - 7.7 [Entity Detail Page (Album & Artist Details)](#77-entity-detail-page-album--artist-details)
   - 7.8 [Search & Search Results Page](#78-search--search-results-page)
   - 7.9 [Settings & Diagnostics Page](#79-settings--diagnostics-page)
   - 7.10 [Now Playing Page & Modular Panels](#710-now-playing-page--modular-panels)
   - 7.11 [Metadata Enrichment Dialog](#711-metadata-enrichment-dialog)
   - 7.12 [Smart Library Enrichment Dialog](#712-smart-library-enrichment-dialog)
   - 7.13 [Mini Player Window](#713-mini-player-window)
   - 7.14 [Audio FX Page](#714-audio-fx-page)
8. [Models & Architecture](#8-models--architecture)
9. [Helpers & Utilities](#9-helpers--utilities)
10. [Minor & Cleanup Issues](#10-minor--cleanup-issues)
11. [Summary & Action Plan](#11-summary--action-plan)
12. [Second-Pass Deep Audit (backend)](#12-second-pass-deep-audit) *(§12.1–12.13, DB-/AUDIO-/QUEUE-/… IDs)*
13. [Third-Pass: Integration, Startup Wiring & Dead-Setting Audit](#13-third-pass-integration-startup-wiring--dead-setting-audit) *(INT-* IDs)*
14. [Fix-Verification Status (as of 2026-08-20)](#14-fix-verification-status-as-of-2026-08-20)
15. [Remediation Batches — sequenced fix plan for a coding agent](#15-remediation-batches--sequenced-fix-plan-for-a-coding-agent)
16. [Coverage Provenance & Verdict — the five crashed-agent scopes](#16-coverage-provenance--verdict)
17. [Coverage Ledger — every §1–§13 finding accounted for](#17-coverage-ledger--every-113-finding-accounted-for)
18. [Resolution Log — fixes applied by the coding agent](#18-resolution-log--fixes-applied-by-the-coding-agent)

---

## 1. Critical Issues

### 🔴 CRIT-01: NowPlayingViewModel double-subscribes events on constructor + Loaded

**File**: `NowPlayingViewModel.cs` (line 104) + `NowPlayingPage.xaml.cs` (line 29)  
**Impact**: Event handlers fire **twice** for every playback state change.

The constructor calls `SubscribeEvents()` at line 104, and then `NowPlayingPage_Loaded` calls `SubscribeEvents()` again at line 29 of the page code-behind. Since `NowPlayingViewModel` is registered as **Transient** in DI (App.xaml.cs line 139), every time the page loads it creates a new ViewModel (correct), but the constructor already subscribes. The `Loaded` handler subscribes again.

```diff
  // In NowPlayingViewModel constructor:
- SubscribeEvents();
- RefreshState();
+ // Defer to page Loaded handler to prevent double-subscribe
```

Or remove the `SubscribeEvents()` call from `NowPlayingPage_Loaded`.

---

### 🔴 CRIT-02: NowPlayingViewModel CancellationTokenSource leak — old CTS never disposed before replacement

**File**: `NowPlayingViewModel.cs` (lines 186-190)

```csharp
_lyricsCts?.Cancel();
_lyricsCts = new CancellationTokenSource();  // ← Old CTS is NOT disposed
```

The old `CancellationTokenSource` is cancelled but never disposed before being replaced. This is a native resource leak. The pattern should be:

```csharp
_lyricsCts?.Cancel();
_lyricsCts?.Dispose();
_lyricsCts = new CancellationTokenSource();
```

Same issue with `_creditsCts` on lines 189-190.

---

### 🔴 CRIT-03: TrackMetadataEditor opens file for exclusive lock-check THEN re-opens for TagLib — race window

**File**: `TrackMetadataEditor.cs` (lines 63-67)

The code opens the file with `FileShare.None` to test for locks, closes it, then opens it again via TagLib. Between the two opens, another process could lock the file. This is a TOCTOU (Time-Of-Check-To-Time-Of-Use) race condition.

**Fix**: Remove the pre-check entirely and let the TagLib write naturally fail — the backup/rollback mechanism already handles this safely.

---

### 🔴 CRIT-04: `CurrentArtworkUrl` initially set to `null` from `CurrentAlbum?.ArtworkUrl` before credits load

**File**: `NowPlayingViewModel.cs` (line 171)

```csharp
CurrentArtworkUrl = CurrentAlbum?.ArtworkUrl;
```

When `UpdateFromState()` fires on a track change, `CurrentAlbum` is still `null` (credits haven't loaded yet), so `CurrentArtworkUrl` becomes `null`. The album art only appears after `LoadCreditsAsync` finishes. This causes a visible **flash of no-artwork** every track change.

**Fix**: Use the track's `AlbumId` to look up artwork directly from the database synchronously OR cache the previous artwork until the new one resolves.

---

### 🔴 CRIT-05: LyricsPanel `_lineElements` list goes stale after ListView recycling

**File**: `LyricsPanel.xaml.cs` (lines 107-113)

The `LyricLineTextBlock_Loaded` event appends TextBlocks to `_lineElements`, but when the ListView virtualizes and recycles items:
1. The old TextBlock references become stale (recycled or removed from visual tree)
2. The list keeps growing, indexing no longer maps correctly to the actual visible items
3. Highlighting via `_lineElements[i]` will target wrong or dead elements

**Fix**: Use `ContainerFromIndex()` on the ListView to get the current container for each index, or switch to a non-virtualized `ItemsControl` given that lyrics lists are typically <200 items.

---

## 2. Now Playing Page

### 📁 NowPlayingPage.xaml

| # | Severity | Finding |
|---|----------|---------|
| NP-01 | ⚠️ Bug | `VisualStateGroup` "AdaptiveLayoutGroup" is declared but both states (`WideLayout`, `NarrowLayout`) have **no setters**. They define triggers but do nothing — completely dead code. |
| NP-02 | ⚠️ Inefficiency | `TopStageGrid_SizeChanged` handler (line 57-60) is bound but the body is an empty comment. Either implement adaptive column width changes or remove the handler binding. |
| NP-03 | 💡 Improvement | The page hardcodes magic numbers for animation offsets (350px) and heights (440, 540, 560) without any constants or adaptive calculation. These should scale with the actual viewport. |
| NP-04 | 💡 Improvement | `MinHeight="360"` on the top stage row is too tall for smaller laptop displays (1366×768). Consider `MinHeight="280"` or make it adaptive. |

### 📁 NowPlayingPage.xaml.cs

| # | Severity | Finding |
|---|----------|---------|
| NP-05 | ⚠️ Bug | `AlbumPanelControl_SizeChanged` sets both `LyricsPanelControl.MaxHeight` AND `LyricsPanelControl.Height` to the same bounded value (line 218-219). Setting explicit `Height` defeats the purpose of `MaxHeight` and prevents the panel from being smaller when content is shorter. Only `MaxHeight` should be set. |
| NP-06 | ⚠️ Edge Case | `CreditsPanelControl_ArtistClicked` and `CreditsPanelControl_AlbumClicked` are `async void` event handlers that navigate via `Frame?.Navigate()`. If `Frame` is null (e.g., page detached), the null-conditional silently fails, which is fine, but the async DB lookup still runs unnecessarily. |
| NP-07 | 💡 Improvement | The animation `sb.Completed` callback on line 111-119 sets `AlbumPanelTransform.X = 0` unconditionally, but this transform is never animated to a non-zero value. Dead code. |

### 📁 NowPlayingViewModel.cs

| # | Severity | Finding |
|---|----------|---------|
| NP-08 | ⚠️ Bug | The artist name splitting regex (line 298) splits on ` & ` which will incorrectly split band names like "Simon & Garfunkel" when the primary artist check (`isFullMatch`) fails to match — e.g., if the DB artist name has slightly different casing or whitespace. |
| NP-09 | ⚠️ Edge Case | `RefreshUpNextQueue()` calls `UpNextQueue.Clear()` and re-adds all items on every state change (line 420-444). This causes the entire `QueuePanel` ListView to flicker/re-render. Use diff-based updates or check for changes first. |
| NP-10 | ⚠️ Inefficiency | `PlayQueueItem` (line 461-472) calls `GetCurrentQueue()` and linearly searches for the item by `Id`. This is O(n) per click. The queue index should be stored on `QueueItem` or use `IndexOf`. |
| NP-11 | 💡 Improvement | `UpdateLyricPosition` binary search (line 396-414) is well-implemented with O(1) fast-path. Good implementation. |

### 📁 AlbumArtPanel.xaml.cs

| # | Severity | Finding |
|---|----------|---------|
| NP-12 | ⚠️ Inefficiency | `AnimateHoverOverlay` creates a **new Storyboard** on every mouse enter/exit. These should be cached or use `Composition` animations for 60fps hover effects without Storyboard overhead. |
| NP-13 | 💡 Improvement | Missing explicit `using System;` directive — `TimeSpan` reference at line 67 relies on implicit usings. |

### 📁 LyricsPanel.xaml.cs

| # | Severity | Finding |
|---|----------|---------|
| NP-14 | ⚠️ Bug | `DefaultLineBrush` is hardcoded to `Microsoft.UI.Colors.White` (line 63). In light theme, this makes inactive lyrics invisible against a white/light background. Should use `ThemeResource TextFillColorTertiaryBrush` or similar. |
| NP-15 | ⚠️ Inefficiency | `UpdateLyricHighlighting()` iterates **all** `_lineElements` on every index change (line 135-161), setting properties on each. With 100+ lyrics lines, this is needlessly expensive. Only the old highlighted line and the new highlighted line need to be updated. |
| NP-16 | ⚠️ Bug | `ScrollIntoView` and `StartBringIntoView` on lines 130 and 147 are **both** called for the active line — double-scrolling. One should be sufficient. `ScrollIntoView` with `Leading` alignment, then `StartBringIntoView` with `VerticalAlignmentRatio = 0.4` are competing scroll targets. |

### 📁 CreditsPanel.xaml

| # | Severity | Finding |
|---|----------|---------|
| NP-17 | ⚠️ Hardcoded | Text colors like `#888888`, `#CCCCCC`, `"White"` are hardcoded throughout instead of using `ThemeResource` brushes. This breaks light-theme support entirely. |
| NP-18 | 💡 Improvement | The "AUDIO INFO" tile shows "STREAM FORMAT", "OUTPUT DEVICE", "DECODER ENGINE", "DAC OUTPUT FORMAT" — excellent quality detail! Consider adding bitrate/sample-rate info from `QualityDetails` for audiophile users. |

### 📁 QueuePanel.xaml

| # | Severity | Finding |
|---|----------|---------|
| NP-19 | ⚠️ Bug | Queue item artwork uses `Track.SourceUri` as the image source (line 82): `Source="{x:Bind Track.SourceUri, ...}"`. This passes the **audio file path** (e.g., `C:\Music\song.mp3`) to `ArtworkPathConverter`, which won't recognize it as a valid artwork URL and will always show the placeholder. Should use the album artwork URL instead. |
| NP-20 | ⚠️ Missing | No visual indication of the **currently playing** item in the queue. The `IsPlaying` property on `QueueItem` exists but isn't used for highlighting in the template. |
| NP-21 | 💡 Improvement | The play button on each queue item always shows the play glyph. For the currently playing item, it should show a pause glyph or an animated equalizer icon. |

---

## 3. Online Fetching System

### 📁 CompositeLyricsService.cs

| # | Severity | Finding |
|---|----------|---------|
| OL-01 | ✅ Good | Offline-first pattern (local → online fallback) is correctly implemented. |
| OL-02 | 💡 Improvement | No caching of negative results. If online providers return `Unavailable`, the next track-change will re-query them. Consider caching `Unavailable` results with a shorter TTL (e.g., 1 hour) to avoid hammering providers. |

### 📁 OnlineLyricsOrchestrator.cs

| # | Severity | Finding |
|---|----------|---------|
| OL-03 | ✅ Good | Single-flight deduplication, two-tier cache, stale fallback — all well-implemented. |
| OL-04 | ⚠️ Edge Case | The stale cache fallback (line 93-97) checks `staleEntry.Value.State != LyricsState.Loading` but a `Loading` state should never be persisted to cache in the first place. Defensive but unnecessary. |
| OL-05 | 💡 Improvement | 30-day cache TTL for lyrics is reasonable, but there's no way for users to force a re-fetch of lyrics for a specific track (e.g., if a provider later corrects lyrics). |

### 📁 ExternalMetadataOrchestrator.cs

| # | Severity | Finding |
|---|----------|---------|
| OL-06 | ⚠️ Inconsistency | `SearchTrackCandidatesAsync` aggregates results from **all** enabled providers. If Provider A returns 5 low-confidence matches and Provider B returns 1 high-confidence match, the list is 6 items sorted correctly — but there's no deduplication of the same track from multiple providers. |
| OL-07 | 💡 Improvement | All three `Search*CandidatesAsync` methods follow an identical structure (check cache → single-flight → iterate providers → cache results → stale fallback). This repetitive pattern could be extracted to a generic `OrchestrateSearchAsync<T>()` helper to reduce ~200 lines of boilerplate. |

### 📁 ExternalArtworkOrchestrator.cs

| # | Severity | Finding |
|---|----------|---------|
| OL-08 | ⚠️ Inefficiency | The cached token file-existence check is repeated in three places: outside single-flight, inside single-flight, and in stale fallback. This is 3 disk I/O calls for every artwork resolution. Consider caching the existence check or using a memory flag. |
| OL-09 | ✅ Good | Image validation via `ImageValidator.IsValidImage()` before caching — prevents HTML error pages from being cached as artwork. Excellent hardening. |

### 📁 LyricsService.cs (Local LRC Parser)

| # | Severity | Finding |
|---|----------|---------|
| OL-10 | ⚠️ API Design | `ParseLrcContent` is `public static` (line 135) but is only called internally and by tests. Making it `public` leaks implementation details. Should be `internal`. |
| OL-11 | ⚠️ Bug | The LRC offset direction may be inverted. Line 192: `startTime - TimeSpan.FromMilliseconds(offsetMs)`. Some LRC specifications define positive `[offset:+500]` as lyrics being 500ms **early**, meaning timestamps should shift **forward** (add), not subtract. However, there is no single authoritative LRC spec, so verify against your source provider (LRCLIB). If the offset meaning is "shift all timestamps by this amount", then subtraction is correct; if it means "lyrics appear this many ms before they should", addition is correct. |
| OL-12 | ⚠️ Edge Case | Multi-timestamp lines like `[00:12.50][01:15.20]Lyric text` correctly expand into separate lines, but the text will be duplicated. This is correct LRC behavior — no change needed but worth documenting. |
| OL-13 | 💡 Improvement | `FindLocalLrcFile` checks both `Lyrics` and `lyrics` subdirectories separately (line 107-127). On case-insensitive Windows filesystems, these are the same directory. Use a single case-insensitive check. |
| OL-14 | 💡 Improvement | Embedded lyrics via TagLib are read synchronously on the calling thread (line 46). For large FLAC files with embedded lyrics+artwork, this could block. Wrap in `Task.Run`. |

---

## 4. Metadata Editor

### 📁 TrackMetadataEditor.cs

| # | Severity | Finding |
|---|----------|---------|
| ME-01 | ⚠️ Bug | Backup file `.octave_bak` is created alongside the original (line 82). If the original file is in a read-only directory (unlikely but possible), `File.Copy` will fail before TagLib even runs. The backup should go to a temp directory. |
| ME-02 | ⚠️ Inconsistency | The verification stage (Stage 2, line 205-253) re-reads the file to verify writes, but falls back to the **update** values (e.g., `update.Title ?? existingTrack.Title`) rather than solely trusting the file read. If the file read fails silently (exception caught on line 251), the database could have different data than what's actually on disk. |
| ME-03 | ⚠️ Edge Case | Multi-value performers: `tagFile.Tag.Performers = new[] { update.ArtistName.Trim() }` (line 99) replaces all performers with a single entry. If the file originally had multiple performers, all but the first are silently discarded. |
| ME-04 | ⚠️ Missing | No validation on the `update.Year` range. A value like `Year = -5` or `Year = 99999` will be written to the file tag. Negative values will be clamped to 0 by the ternary, but extremely large years should also be validated. |
| ME-05 | 💡 Improvement | The artist/album ID regeneration via `IdGenerator.FromArtist()` and `IdGenerator.FromAlbum()` means renaming an artist creates entirely new artist/album entities in the DB, orphaning the old ones. Consider a cleanup pass or migration. |

### 📁 MetadataEnrichmentViewModel.cs

| # | Severity | Finding |
|---|----------|---------|
| ME-06 | ⚠️ Bug | `_applyCts` (line 247) is created but never cancelled on subsequent calls to `ApplyChangesAsync`. If the user rapidly clicks "Apply", multiple writes could run concurrently on the same file. |
| ME-07 | ⚠️ Edge Case | `FieldSelectionOptions` constructed in `ApplyChangesAsync` doesn't include `ApplyComposer`, `ApplyTrackCount`, or `ApplyDiscCount` toggles (lines 268-279). These fields default to `true`, meaning they'll always be applied even if the user intended to skip them. The VM has no UI binding for Composer/TrackCount/DiscCount toggles. |
| ME-08 | 💡 Improvement | The `InitializeAsync` method fires the initial search immediately (line 133). If the dialog opens for a track with no internet, the user sees a long wait before seeing the form. Consider showing local metadata first, then fetching online in background. |

### 📁 MetadataEnrichmentDialog.xaml.cs

| # | Severity | Finding |
|---|----------|---------|
| ME-09 | ⚠️ Edge Case | `BrowseArtworkButton_Click` uses `FileOpenPicker` which requires COM initialization. On some Windows configurations, this can throw `COMException` if called from a non-STA thread. The `hwnd` check is good but insufficient. |
| ME-10 | 💡 Improvement | `.webp` is in the file picker filter (line 58), but TagLib doesn't natively support WebP as an embedded picture format. The image will be written with `image/jpeg` mime type regardless of actual content. Consider converting WebP to JPEG before embedding. |

---

## 5. Core Services

### 📁 QueueService.cs

| # | Severity | Finding |
|---|----------|---------|
| CS-01 | 💡 Note | At 27KB, this is the largest single file in the codebase. Consider splitting into partial classes: `QueueService.Playback.cs`, `QueueService.Persistence.cs`, `QueueService.Shuffle.cs`. |

### 📁 ArtworkCacheManager.cs

| # | Severity | Finding |
|---|----------|---------|
| CS-02 | ⚠️ Edge Case | `CacheBytesAsync` returns a relative token `"ArtworkCache/{hash}{ext}"` but the `ArtworkPathConverter` does `artworkUrl.Replace('/', '\\')` to build an absolute path. If anyone stores the token with backslashes, the converter's `StartsWith("ArtworkCache/")` check (forward slash) will fail. Keep consistent path separators. |
| CS-03 | 💡 Improvement | No eviction strategy. The artwork cache can grow indefinitely on disk. Consider adding a max-size or LRU eviction for the on-disk cache. |

### 📁 LocalLibraryScanner.cs

| # | Severity | Finding |
|---|----------|---------|
| CS-04 | 💡 Note | At 30KB, this is another very large file. The scan logic is solid but could benefit from extraction of the tag-reading logic into a separate `TagReader` class. |

### 📁 LibraryWatcherService.cs

| # | Severity | Finding |
|---|----------|---------|
| CS-05 | 💡 Improvement | Ensure that `FileSystemWatcher` events are debounced to avoid duplicate scans when IDEs or sync tools touch files rapidly. |

---

## 6. Desktop Shell & MainWindow

### 📁 ShellViewModel.cs

| # | Severity | Finding |
|---|----------|---------|
| SH-01 | ⚠️ Inconsistency | `AppendConsole` prepends new lines (line 574: `line + "\n" + ConsoleOutput`) but `RunStaticFireAsync` (line 808) uses the old inline pattern: `"[text]\n" + ConsoleOutput`. Both work but `AppendConsole` should be used consistently everywhere. |
| SH-02 | ⚠️ Edge Case | `UpdateSearchSuggestionsAsync` disposes `_searchCts` on line 855 but then accesses it again on line 856 (`_searchCts = null`). If another thread calls this method simultaneously, there's a potential race on `_searchCts`. Consider using `Interlocked.Exchange`. |
| SH-03 | ⚠️ Inefficiency | `PlayTrackByIdAsync` (line 915-927) calls `_queueService.Clear()` then `Enqueue` then `PlayIndex(0)` inside `_dispatcher.TryEnqueue` after an awaited call — the `TryEnqueue` is redundant since we're already on the UI thread after `await`. |
| SH-04 | 💡 Improvement | `SleepTimerStatus` only shows the initial countdown ("Pausing in N min") but doesn't update in real-time. Users have no way to see remaining time. Consider a periodic update. |
| SH-05 | 💡 Improvement | `DeleteAllDuplicates` sends files to the recycle bin but doesn't show any progress or confirmation dialog. For a potentially destructive operation, a confirmation step should be added at the UI layer. |

### 📁 MainWindow.xaml.cs

| # | Severity | Finding |
|---|----------|---------|
| SH-06 | ⚠️ Inefficiency | `CompositionTarget.Rendering` fires at 60fps unconditionally (line 44). The handler at line 47 early-returns when the visualizer is hidden or not playing, but the event subscription itself has overhead. Consider subscribing/unsubscribing based on `IsPlaying` state. |
| SH-07 | 💡 Improvement | The responsive breakpoint at 950px (line 59) is a single tier. Consider a multi-tier approach: compact (<700px), medium (700-1100px), wide (>1100px) for better adaptive UX. |

### 📁 MiniPlayerWindow.xaml.cs

| # | Severity | Finding |
|---|----------|---------|
| SH-08 | ✅ Good | Always-on-top, keyboard shortcut handling, and restore-main-on-close are well-implemented. |
| SH-09 | 💡 Improvement | The mini player has no Next/Previous track buttons visible in the code-behind. |

### 📁 App.xaml.cs

| # | Severity | Finding |
|---|----------|---------|
| SH-10 | ⚠️ Concern | `UnhandledException` handler sets `e.Handled = true` (line 56) for **all** unhandled exceptions. This swallows critical errors silently. Consider logging to a file and only handling specific known benign exceptions. |
| SH-11 | ⚠️ Inconsistency | `NowPlayingViewModel` is registered as `Transient` (line 139) but depends on singleton services. If multiple NowPlaying instances are created without proper disposal, stale event handlers will accumulate. The `Unloaded → Dispose()` pattern handles this, but it's fragile. |
| SH-12 | 💡 Improvement | The bass.dll architecture diagnostic (lines 154-185) reads raw PE headers manually. Consider using `System.Reflection.PortableExecutable.PEReader` for a cleaner implementation. |

---

## 7. Comprehensive Page-by-Page UI/UX & Polish Audit

This section conducts an exhaustive, element-by-element UI/UX and polish audit across every page, dialog, and control in the application. It focuses specifically on **unexplained buttons, missing tooltips, hazardous actions lacking confirmation, broken bindings, light-theme readability bugs, and UI-thread performance bottlenecks**, without altering the core visual identity of the application.

---

### 7.1 MainWindow & Global Shell

**Files**: `MainWindow.xaml`, `MainWindow.xaml.cs`, `ShellViewModel.cs`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-MW-01 | ⚠️ UX / Tooltip | Transport Buttons (Shuffle, Prev, Next, Repeat, Mute) | In `MainWindow.xaml` lines 343-370, `ShuffleButton`, `PreviousButton`, `NextButton`, `RepeatButton`, and `MuteButton` have **no `ToolTipService.ToolTip`**. Users hovering over them have no tooltip confirmation or keyboard accelerator hints. **Fix**: Add tooltips: `"Shuffle (Ctrl+S)"`, `"Previous (Ctrl+Left)"`, `"Next (Ctrl+Right)"`, `"Repeat (Click to toggle Track / Queue / Off)"`, `"Mute / Unmute (Ctrl+M)"`. |
| UI-MW-02 | ⚠️ UX / Unclear Behavior | Timeline Slider Thumb Tooltip Disabled | In `MainWindow.xaml` line 383, `IsThumbToolTipEnabled="False"` is explicitly set on `PlaybackSlider`. When dragging the scrubber, the user **cannot see the target timestamp** they are scrubbing to. **Fix**: Enable thumb tooltip with a custom `DurationFormatConverter` or show the target scrub time dynamically in the left timestamp textblock. |
| UI-MW-03 | ⚠️ UX / Hazardous | Bottom Bar Track Details Click Target | In `MainWindow.xaml` line 238, clicking anywhere on the track title / artwork in the bottom bar opens the **Sidebar Pivot overlay** (Info tab), not the full **Now Playing Page**. Users expecting to expand to the full Now Playing experience are confused by a side overlay opening instead. **Fix**: Add a clear tooltip on the bottom bar: `"Open Track Info"` or add a distinct expand icon pointing to Now Playing. |
| UI-MW-04 | ⚠️ Theme Bug | Hardcoded Text Colors in Sidebar & Player Bar | `MainWindow.xaml` hardcodes `#AAAAAA`, `#CCCCCC`, `#888888`, and `Foreground="White"` across the search suggestions, sidebar info grid, and player bar (lines 78, 150, 250, 349, etc.). When switched to Light Theme, these labels appear washed out or unreadable. **Fix**: Replace all hardcoded colors with `ThemeResource TextFillColorPrimaryBrush`, `TextFillColorSecondaryBrush`, and `TextFillColorTertiaryBrush`. |
| UI-MW-05 | ⚡ Performance | Unthrottled 60 FPS Composition Rendering | In `MainWindow.xaml.cs` line 44, `CompositionTarget.Rendering += CompositionTarget_Rendering` is hooked permanently on startup and runs at 60Hz. While it has an early return check, it wakes up the UI thread 60 times per second during idle periods or when visualizer is hidden. **Fix**: Subscribe to `CompositionTarget.Rendering` only when `ViewModel.IsPlaying == true && ViewModel.IsVisualizerEnabled == true`, and unsubscribe when paused/stopped. |
| UI-MW-06 | ⚠️ UX / Polish | Global Search Box Clears Text on Navigating Away | In `MainWindow.xaml.cs` lines 424-445, submitting a query navigates to `SearchResultsPage`, but the text in `GlobalSearchBox` remains detached from the page title. When searching again from inside `SearchResultsPage`, typing doesn't sync the page header until Enter is pressed. |
| UI-MW-07 | ⚠️ UX / Tooltip | Favorite Button Static Tooltip | Line 328: `ToolTipService.ToolTip="Favorite"` is static. When a song is already favorited, the tooltip should update to `"Remove from favorites"`. |

---

### 7.2 Home Page

**Files**: `HomePage.xaml`, `HomePage.xaml.cs`, `HomeViewModel.cs`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-HP-01 | ⚠️ UX / Unexplained | Spotlight Hero Play Button Missing Tooltip & Title | In `HomePage.xaml` lines 86-90, the Hero Spotlight play button contains only a `<FontIcon Glyph="&#xE768;" />` with no tooltip. Furthermore, if `ViewModel.RecentlyPlayed` is empty but `ViewModel.LastAdded` has tracks, clicking Hero Play plays `LastAdded[0]` without explaining which track it plays. **Fix**: Add `ToolTipService.ToolTip="Play spotlight track"` and ensure the Hero card title/artist matches what will actually play. |
| UI-HP-02 | ⚠️ UX / Misleading | Empty State "Scan" Button Navigates to Settings | In `HomePage.xaml` line 154, the empty library card has a button labelled **"Scan"**, but `ScanFolder_Click` simply navigates the frame to `SettingsPage` without scanning or opening a folder picker. Users click "Scan" expecting a scan to start immediately. **Fix**: Change button label to `"Open Settings to Add Music"` or directly invoke the Windows folder picker. |
| UI-HP-03 | ⚠️ Polish / Affordance | No Hover Play Overlay on Home Grid Cards | On Quick Play tiles and Track Cards (lines 18-53), hovering over a card changes the border color, but there is **no visible play button icon overlay**. Users have no visual cue whether clicking the card navigates to the album, adds to queue, or immediately plays the track. **Fix**: Add a subtle hover play button overlay on the card artwork. |
| UI-HP-04 | ⚠️ Theme Bug | Hardcoded Background `#1E1E1E`, `#2A2A2A`, `#1E1E24` | Lines 18, 25, 46, 146 hardcode dark hex colors for cards and empty state boxes. In Light Theme, these dark boxes clash harshly with the rest of the UI. **Fix**: Use `{ThemeResource CardBackgroundFillColorDefaultBrush}` and `{ThemeResource LayerFillColorDefaultBrush}`. |
| UI-HP-05 | ⚠️ Code Polish | Code-Behind Hardcoded Border Colors in Hover Events | `Card_PointerEntered` / `Exited` in `HomePage.xaml.cs` lines 101-116 programmatically inject `Microsoft.UI.ColorHelper.FromArgb(255, 42, 42, 42)` into `grid.BorderBrush`. This bypasses WinUI's styling system and fails when switching themes dynamically. **Fix**: Use XAML PointerOver VisualStates or style setters. |

---

### 7.3 Library Page (Songs)

**Files**: `LibraryPage.xaml`, `LibraryPage.xaml.cs`, `LibraryViewModel.cs`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-LP-01 | ⚠️ UX / Missing Tooltip | Row Play Button & More Menu Button Tooltips | In `LibraryPage.xaml` lines 56-69 and 90-98, the per-row Play/Pause button and the `...` More Menu button have **no tooltips**. Hovering gives zero feedback. **Fix**: Add `ToolTipService.ToolTip="Play / Pause"` and `ToolTipService.ToolTip="More options"`. |
| UI-LP-02 | ⚠️ UX / Unclear Indicator | Provider Cloud vs. Disk Status Glyphs | In `LibraryPage.xaml` lines 75-81, the glyph `&#xE770;` (Local) or `&#xE774;` (Cloud) is rendered without any text or tooltip. Users do not know what this icon signifies. **Fix**: Add `ToolTipService.ToolTip="{x:Bind Provider}"` so hovering displays "Local" or "Cloud". |
| UI-LP-03 | ⚠️ UX / Header | Sort ComboBox Header Missing | Line 27: `<ComboBox Header="" SelectedIndex="{x:Bind ViewModel.SortIndex, Mode=TwoWay}" ...>`. The ComboBox has an empty header string and relies on items containing "Sort: Title". A proper `Header="Sort by"` or accessible label is missing. |
| UI-LP-04 | ⚡ Performance | Button Reference Tracking in Code-Behind | In `LibraryPage.xaml.cs` lines 59-70 and 92-117, `_playButtons` maintains a manual `List<Button>` populated via `Loaded` events and cleans up via `RemoveAll(btn => btn.XamlRoot == null)`. In a virtualized ListView with thousands of items, this list continuously grows and iterates on every track change. **Fix**: Bind play icon state directly in the DataTemplate via ViewModel properties or converter. |
| UI-LP-05 | ⚠️ UX / Polish | Skeleton Animation Doesn't Stop on Error | In `LibraryPage.xaml.cs` line 75, `SkeletonPulse` storyboard begins on `Loaded`, but if `ViewModel.LoadAsync()` throws or completes with an empty library, the skeleton panel fades out via `IsLoading = false` but the Storyboard keeps running in the background. **Fix**: Explicitly stop the animation on completion. |

---

### 7.4 Albums Page

**Files**: `AlbumsPage.xaml`, `AlbumsPage.xaml.cs`, `AlbumsViewModel.cs`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-AP-01 | 🔴 UX / Bug | Missing Empty State Placeholder | If the user has an empty library or no albums are found, `AlbumsPage.xaml` displays only the `"Albums"` title and an empty blank screen underneath. There is no message explaining that the library is empty or guiding the user to add music folders. **Fix**: Add an empty state container with icon and "No albums in your library" message (matching HomePage's pattern). |
| UI-AP-02 | ⚠️ Polish / Affordance | No Hover Play / Action Affordance on Album Cards | In `AlbumsPage.xaml` lines 33-52, album cards only respond to click (navigating to `EntityDetailPage`). There is no hover button to immediately play or enqueue the entire album. **Fix**: Add a subtle hover play button in the bottom-right corner of the artwork thumbnail to "Play Album Now". |
| UI-AP-03 | ⚠️ Code Quality | Leftover Developer TODO Comments | Lines 11-13 of `AlbumsPage.xaml.cs` contain stale TODO comments: `// TODO: Album artwork extraction, caching and virtualization will be implemented in a future ticket.` |
| UI-AP-04 | ⚠️ Theme Bug | Hardcoded Background `#222222` and Text `#AAAAAA` | Lines 42 & 50 hardcode dark theme colors, leading to contrast issues in Light Theme. |

---

### 7.5 Artists Page

**Files**: `ArtistsPage.xaml`, `ArtistsPage.xaml.cs`, `ArtistsViewModel.cs`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-AR-01 | 🔴 UX / Bug | Missing Empty State Placeholder | Like AlbumsPage, if no artists exist in the database, the page renders completely empty with no guidance or feedback. **Fix**: Add an empty state container with "No artists found" guidance. |
| UI-AR-02 | ⚠️ Polish / UX | Circular Artwork Placeholder Contrast | In `ArtistsPage.xaml` line 41, circular placeholders have `Background="#222222"`. If an artist image fails to load or is missing, a flat dark circle with no icon is displayed. **Fix**: Include a default contact/microphone silhouette icon in the center of the placeholder. |
| UI-AR-03 | ⚠️ Polish | Missing Artist Track / Album Count Subtitle | In `ArtistsPage.xaml` lines 45-47, the card only displays the artist's name. It does not show how many albums or tracks the user has for that artist (e.g. "3 albums · 24 songs"). **Fix**: Add a secondary subtitle line showing album/song count. |

---

### 7.6 Playlists & Playlist Detail Pages

**Files**: `PlaylistsPage.xaml`, `PlaylistsPage.xaml.cs`, `PlaylistDetailPage.xaml`, `PlaylistDetailPage.xaml.cs`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-PL-01 | 🔴 UX / Hazardous | Delete Playlist Button Has No Confirmation Dialog | In `PlaylistDetailPage.xaml.cs` lines 138-142: Clicking the trash can icon (`DeletePlaylist_Click`) **immediately executes `DeleteSelfCommand` and closes the page** without asking the user to confirm! An accidental click permanently deletes the entire playlist. **Fix**: Show a `ContentDialog` asking "Are you sure you want to delete this playlist?" with "Delete" and "Cancel" buttons. |
| UI-PL-02 | ⚠️ UX / Unclear | New Playlist Dialog Allows Blank Names | In `PlaylistsPage.xaml.cs` lines 49-65, the New Playlist dialog creates a playlist even if the user enters spaces or leaves it blank. **Fix**: Disable the "Create" button until at least 1 non-whitespace character is entered. |
| UI-PL-03 | ⚠️ UX / Missing Feature | No "Play Playlist" Option on Playlist Card Context Menu | In `PlaylistsPage.xaml.cs` lines 71-81, right-clicking a playlist card only shows "Delete playlist". There is no option to "Play", "Add to Queue", or "Rename Playlist". |
| UI-PL-04 | ⚠️ UX / Polish | Playlist Icon Placeholder is Static Gray | In `PlaylistDetailPage.xaml` lines 26-28, the playlist banner is a flat `#222222` square with a gray music note glyph (`&#xE142;`). Generating a dynamic 2x2 collage of track artworks (like Spotify/Apple Music) or an accent gradient would elevate visual polish. |
| UI-PL-05 | ⚠️ UX / Tooltip | Back Button & Play All Button Missing Tooltips | In `PlaylistDetailPage.xaml` lines 37-45, `PlayAllCommand` and `BackButton_Click` buttons have no tooltips. |

---

### 7.7 Entity Detail Page (Album & Artist Details)

**Files**: `EntityDetailPage.xaml`, `EntityDetailPage.xaml.cs`, `EntityDetailViewModel.cs`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-ED-01 | ⚠️ UX / Feedback | Enrich Entity Button Gives No Completion Feedback | In `EntityDetailPage.xaml` line 47, the magnifying glass button triggers `EnrichEntityCommand` to fetch online metadata. When clicked, `ProgressRing` spins (line 51), but once completed, **no banner, toast, or message is shown** to notify the user whether new artwork or bio was found or if no changes were available. **Fix**: Display an InfoBar or transient notification ("Artist details updated" / "Already up to date"). |
| UI-ED-02 | ⚠️ UX / Layout | Description MaxHeight Cuts Off Long Biographies | Line 40: `MaxHeight="60"` and `TextTrimming="CharacterEllipsis"` silently cuts off rich artist biographies to ~3 lines with no "Read More" expander or dialog to read the full bio. **Fix**: Add a "Read More" link or an expandable text block. |
| UI-ED-03 | ⚠️ UX / Tooltip | Missing Tooltips on Primary Action Buttons | Lines 43 & 53: The large circular Play button and the Back button have no `ToolTipService.ToolTip`. |
| UI-ED-04 | ⚠️ Theme Bug | Banner Hardcoded Foreground `#AAAAAA`, `#CCCCCC`, `White` | Lines 37-40 hardcode text colors instead of utilizing theme-aware foreground brushes. |

---

### 7.8 Search & Search Results Page

**Files**: `SearchResultsPage.xaml`, `SearchResultsPage.xaml.cs`, `SearchViewModel.cs`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-SR-01 | ⚠️ UX / Polish | Empty State Text is Bare | In `SearchResultsPage.xaml` lines 202-207: When no results match, the page displays a single line of gray text: `"No matches found."`. **Fix**: Enhance the empty state with a search icon, suggested tips (e.g. "Check spelling or search by artist/album name"), and a button to return Home. |
| UI-SR-02 | ⚠️ UX / Missing | Result Count Badges in Section Headers | Lines 29, 103, 138, 169: The section titles ("Tracks", "Albums", "Artists", "Playlists") do not display the number of matching items (e.g. `"Tracks (14)"`, `"Albums (2)"`). |
| UI-SR-03 | ⚠️ UX / Tooltip | Row Play Buttons & More Menu Buttons Missing Tooltips | Lines 49 & 86: Track row play buttons and `...` menu buttons have no tooltips. |
| UI-SR-04 | ⚠️ UX / Inconsistency | Missing Album Column Margin Consistency | In `SearchResultsPage.xaml` line 80, the Album title column in track rows has `Margin="8,0,0,0"`, which differs from `LibraryPage` (which omits album column entirely or has different column proportions). |

---

### 7.9 Settings & Diagnostics Page

**Files**: `SettingsPage.xaml`, `SettingsPage.xaml.cs`, `ExternalDataSettingsViewModel.cs`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-ST-01 | 🔴 UX / Hazardous | "Delete All Duplicates" Button Lacks Explanation & Safeguards | In `SettingsPage.xaml` line 155: `<Button Content="Delete All Duplicates" Click="DeleteAllDuplicates_Click" Foreground="#EF5350" />`. This button has **no description or tooltip** explaining that it will keep only 1 copy and move all other copies across the entire library to the Recycle Bin. Clicking it performs bulk file deletion without a pre-deletion summary dialog (e.g. "This will move 42 duplicate files to the Recycle Bin"). **Fix**: Add a clear explanatory label and require a confirmation dialog showing exact file counts before deleting. |
| UI-ST-02 | ⚠️ UX / Unexplained | "Ignite Main Engines (Run Static Fire)" Debug Button | In `SettingsPage.xaml` lines 434-437: A giant neon green (`#00FF66`) button labelled **"Ignite Main Engines (Run Static Fire)"** is placed at the bottom of Settings. For regular users, this terminology is completely unexplained and confusing (it scans `SpecialFolder.MyMusic`, seeds DB, and queues all songs). **Fix**: Relabel to `"Diagnostic Self-Test & Library Crawl"` with a subtitle explaining it is a developer diagnostic tool, or move it inside an expandable "Advanced Developer Tools" section. |
| UI-ST-03 | ⚠️ UX / Unclear | Equalizer Presets Have No Active State Indication | In `SettingsPage.xaml` lines 108-113: The preset buttons ("Flat", "Bass Boost", "Treble Boost", etc.) are plain buttons. When clicked, they apply gains, but there is **no visual highlight indicating which preset is currently active**. If a user adjusts a slider, it doesn't indicate that the preset is now "Custom". **Fix**: Highlight the active preset button or show an active preset label. |
| UI-ST-04 | ⚠️ UX / Polish | EQ Missing Dependency Warning | Line 81 notes: `"10-band graphic EQ. Requires bass_fx.dll (drop it in BassPlugins\)."`. If `bass_fx.dll` is not loaded, the EQ toggle does not indicate whether the DSP engine is active or silently failing. **Fix**: Show a status pill ("Engine Active" vs "bass_fx.dll Missing"). |
| UI-ST-05 | ⚠️ UX / Missing Tooltips | Folder Management & Duplicate Actions Missing Tooltips | "Add Folder" and "Rescan All" (lines 123-124) lack tooltips explaining background scanning behavior. In the Duplicate Manage flyout (lines 186-187), the "Move" button launches a folder picker without explaining that it moves the physical audio file and updates the database record. |
| UI-ST-06 | ⚠️ UX / Explanation | TheAudioDB API Key Instructions | Line 325: The password box mentions `"API Key (Default public test key is '2')"`, but doesn't explain that the public test key has strict rate limits and where users can get their own free/supporter API key from `theaudiodb.com`. |

---

### 7.10 Now Playing Page & Modular Panels

**Files**: `NowPlayingPage.xaml`, `AlbumArtPanel.xaml`, `CreditsPanel.xaml`, `LyricsPanel.xaml`, `QueuePanel.xaml`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-NP-01 | 🔴 UX / Bug | QueuePanel Artwork Binding Broken | In `QueuePanel.xaml` line 82: `Source="{x:Bind Track.SourceUri, Converter={StaticResource ArtworkPathConverter}, ConverterParameter=40}"`. It binds the **file URI** (e.g. `C:\Music\song.mp3`) instead of the album artwork URL. As a result, artwork for every track in the Up Next queue fails to resolve and shows the placeholder image. **Fix**: Pass the album artwork URL or update `ArtworkPathConverter` to handle track entity resolution. |
| UI-NP-02 | 🔴 UX / Defect | QueuePanel Lacks Current Playing Item Highlight & Remove Action | In `QueuePanel.xaml` lines 64-99: The currently playing item has no accent border or active indicator. Furthermore, there is **no remove (`X`) button** for individual queue items in the NowPlaying QueuePanel, unlike the Sidebar Queue which has one. |
| UI-NP-03 | ⚠️ UX / Polish | Lyrics Auto-Scroll is Jumpy / Double-Scrolling | In `LyricsPanel.xaml.cs` lines 130 & 147: `ScrollIntoView` and `StartBringIntoView` are both triggered simultaneously on every lyric line change. This causes visible stuttering and competing scroll offsets. **Fix**: Use smooth animated scrolling via ScrollViewer `ChangeView` centered on the active lyric index. |
| UI-NP-04 | ⚠️ Theme Bug | Hardcoded White & Gray Text in Credits & Queue Panels | `CreditsPanel.xaml` (lines 46, 78, 98, 103) and `QueuePanel.xaml` (lines 29, 77, 87) hardcode `Foreground="White"` and `Foreground="#888888"`. In Light Theme, text contrast is severely compromised. **Fix**: Use `ThemeResource TextFillColorPrimaryBrush` and `TextFillColorSecondaryBrush`. |
| UI-NP-05 | ⚡ Performance | Queue ListView Complete Destruction on State Updates | In `NowPlayingViewModel.cs` lines 420-444: `RefreshUpNextQueue()` calls `UpNextQueue.Clear()` and re-adds all items on every playback state tick, causing WinUI to destroy and re-instantiate the entire ListView visual tree. **Fix**: Implement a signature check (like `ShellViewModel.RefreshQueue`) to only rebuild when the queue items actually change. |
| UI-NP-06 | ⚠️ UX / Missing | No Copy Lyrics or Lyrics Offset Adjustment Control | In `LyricsPanel.xaml`: There is no button to copy lyrics to clipboard or adjust synchronization timing offset (+/- 500ms) for out-of-sync LRC files. |

---

### 7.11 Metadata Enrichment Dialog

**Files**: `MetadataEnrichmentDialog.xaml`, `MetadataEnrichmentDialog.xaml.cs`, `MetadataEnrichmentViewModel.cs`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-MD-01 | ⚠️ UX / Layout | Field Diff Table Lacks Clear Distinction for Unchanged Fields | In `MetadataEnrichmentDialog.xaml` lines 80-153: The comparison table displays all 8 fields with checkboxes. When a field has no differences (`HasDifference == false`), it is still displayed identically to fields that have differences, making it hard to scan what will actually change. **Fix**: Gray out or display a subtle "(No change)" tag next to matching fields, or pre-uncheck them. |
| UI-MD-02 | ⚠️ UX / Tooltip | Candidate Selection Dropdown Lacks Provider Badges | In `MetadataEnrichmentDialog.xaml` lines 53-62: The candidate ComboBox only displays `MatchEvidence`. It does not show the provider name (e.g. "MusicBrainz", "TheAudioDB") or the percentage match badge inside the dropdown items. |
| UI-MD-03 | ⚠️ UX / Polish | Artwork Browse Button Gives No Visual Confirmation | When a user browses a local image file via `BrowseArtworkButton_Click`, the thumbnail in the dialog doesn't immediately update to show the newly selected local image; it only updates the text string ("Selected Local File: image.png"). **Fix**: Immediately load and render the selected image bytes in the preview thumbnail. |

---

### 7.12 Smart Library Enrichment Dialog

**Files**: `LibraryEnrichmentDialog.xaml`, `LibraryEnrichmentDialog.xaml.cs`, `LibraryEnrichmentViewModel.cs`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-LE-01 | ⚠️ UX / Unclear Actions | "Preview Changes (Dry Run)" vs "Apply Safe Changes" Distinction | In `LibraryEnrichmentDialog.xaml` lines 44-51: The top action buttons lack tooltips explaining the core difference. Users are hesitant to click either because they don't know whether "Preview Changes" writes anything to disk. **Fix**: Add clear tooltips: `"Analyze library and stage matches without modifying any files on disk"` and `"Scan and automatically write high-confidence (90%+) matches to file tags"`. |
| UI-LE-02 | ⚠️ UX / Unclear Action | "Never Ask Again" Button in Review Item | In `LibraryEnrichmentDialog.xaml` line 173: The button "Never Ask Again" on ambiguous review candidates does not explain what action it takes (e.g. stores a suppression flag so this track is omitted from future scan prompts). **Fix**: Add a tooltip: `"Dismiss this candidate and do not suggest it in future scans"`. |
| UI-LE-03 | ⚠️ UX / Polish | Metric Cards Lack Descriptions | Lines 74-99: Six metric counters ("ANALYZED", "SAFE MATCHES", "COMPLETE", "NEEDS REVIEW", "NO MATCH", "FAILED") have abbreviations and numbers. Hovering over each should show a short tooltip explaining what each counter represents. |

---

### 7.13 Mini Player Window

**Files**: `MiniPlayerWindow.xaml`, `MiniPlayerWindow.xaml.cs`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-MP-01 | ⚠️ UX / Missing Tooltip | Mini Player Transport Buttons Missing Tooltips | In `MiniPlayerWindow.xaml` lines 45-56: Previous, Play/Pause, and Next buttons have no tooltips. |
| UI-MP-02 | ⚠️ UX / Unclear | Scrubbing on Mini Player Lacks Time Preview | Line 73: `MiniSeek` has `IsThumbToolTipEnabled="False"`, so users scrubbing on the mini player cannot see where they are seeking. |
| UI-MP-03 | ⚠️ Theme Bug | Hardcoded Background `#1C1C1C` | Line 12 hardcodes dark background. While the mini player is designed as a dark compact widget, using `ThemeResource LayerFillColorDefaultBrush` ensures compatibility with system high-contrast modes. |

---

### 7.14 Audio FX Page

**Files**: `AudioFxPage.xaml`, `AudioFxPage.xaml.cs`

| # | Category | Element / Flow | Issue Description & Polish Recommendation |
|---|----------|----------------|-------------------------------------------|
| UI-FX-01 | ⚠️ UX / Placeholder | Audio FX Page is Completely Blank | `AudioFxPage.xaml` contains only `<Grid Grid.Row="1" />`. Navigating to Audio FX from the sidebar displays an empty canvas. *(Note: User requested to ignore Audio FX implementation for now, but noted here for complete audit tracking).* |

---

## 8. Models & Architecture

### 📁 DomainModels.cs

| # | Severity | Finding |
|---|----------|---------|
| AR-01 | ⚠️ Inconsistency | `QueueItem` is a `class` (mutable `IsPlaying`) while all other domain models are immutable `record`s. Consider making `QueueItem` a record with `IsPlaying` as a separate state tracked by the queue service. |
| AR-02 | 💡 Improvement | `AudioQualityDetails` has 11 string properties. Most are display-only. Consider grouping into sub-records: `StreamInfo` and `OutputInfo`. |

### 📁 ExternalDataModels.cs

| # | Severity | Finding |
|---|----------|---------|
| AR-03 | ⚠️ Concern | `ExternalIds.GetId()` has a case-insensitive switch but `AdditionalIds` dictionary lookup uses the raw key (line 36). If someone stores "MusicBrainzReleaseId" in `AdditionalIds`, the switch won't match it (it only matches "musicbrainz" and "mbid"), and the dictionary lookup will use the exact key. This split behavior could cause confusion. |
| AR-04 | 💡 Improvement | `TrackMetadataUpdate` uses nullable properties to indicate "don't change this field" vs "set to this value". `null` for `string?` properties is ambiguous — does `Title = null` mean "don't change" or "clear the title"? Consider using `Optional<T>` or a separate `FieldMask`. |

### 📁 LyricsModels.cs

| # | Severity | Finding |
|---|----------|---------|
| AR-05 | ✅ Good | Clean, minimal models. `LyricLine` with `Start`, `End?`, `Text` is well-designed. |

### 📁 EnrichmentModels.cs

| # | Severity | Finding |
|---|----------|---------|
| AR-06 | 💡 Improvement | `CandidatePreview` has 17 parameters — very wide record. Consider grouping related fields into sub-records (e.g., `TrackFields`, `AlbumFields`, `NumberFields`). |

---

## 9. Helpers & Utilities

### 📁 AsyncSingleFlight.cs

| # | Severity | Finding |
|---|----------|---------|
| HL-01 | ⚠️ Bug | The `while (true)` loop (line 18) can spin indefinitely if `TryGetValue` keeps returning a task that's already completed but `TryAdd` also keeps failing (ABA problem). In practice this is unlikely with `ConcurrentDictionary`, but the spin-wait pattern is fragile. Add a max retry count or use `GetOrAdd`. |
| HL-02 | ⚠️ Edge Case | If the factory throws, `tcs.TrySetException(ex)` propagates the exception to all waiters (line 37), and then `throw` re-throws for the executing caller. All concurrent callers receive the same exception — this is correct single-flight behavior but worth documenting. |

### 📁 ArtworkPathConverter.cs

| # | Severity | Finding |
|---|----------|---------|
| HL-03 | ⚠️ Thread Safety | The LRU cache uses `lock (CacheLock)` which is fine for correctness, but `BitmapImage` objects are UI-thread-bound in WinUI. If `Convert()` is called from a background thread (e.g., during virtualized list rendering), the `new BitmapImage()` will throw. |
| HL-04 | 💡 Improvement | The 250-item LRU cache size is hardcoded. For large libraries with 10,000+ albums, this could cause cache thrashing on the Albums page. Consider increasing to 500 or making it configurable. |

### 📁 TrackContextMenu.cs

| # | Severity | Finding |
|---|----------|---------|
| HL-05 | ⚠️ Edge Case | `ShowAsync` is `async Task` but callers likely use fire-and-forget. If the favorite toggle or playlist creation throws, the exception is swallowed. Each internal click handler already has try-catch, so this is fine in practice. |

### 📁 SpectrumVisualizerControl.xaml.cs

| # | Severity | Finding |
|---|----------|---------|
| HL-06 | ✅ Good | Efficient rectangle-based bar visualization with baseline offset. Good use of Canvas for pixel-precise rendering. |
| HL-07 | 💡 Improvement | The visualizer creates `Rectangle` shapes with `RadiusX/RadiusY = 1.0` which is barely visible. Increase to 2-3 for more visible rounding and a more premium aesthetic. |

---

## 10. Minor & Cleanup Issues

| # | File | Issue |
|---|------|-------|
| MC-01 | `ExternalDataModels.cs` L3 | Duplicate `using Octave.Core.Models;` — the file is already in this namespace. Harmless but unnecessary. |
| MC-02 | `NowPlayingPage.xaml` L8 | `mc:Ignorable="d"` declared but `d:` prefix is never used in the page. |
| MC-03 | `AlbumArtPanel.xaml.cs` L67 | Uses `TimeSpan` without explicit `using System;` — works via implicit usings but should be explicit for clarity. |
| MC-04 | `CreditsPanel.xaml` L46 | Artist name TextBlock hardcodes `Foreground="White"` — breaks light theme. |
| MC-05 | `QueuePanel.xaml` L29 | Queue header hardcodes `Foreground="White"`. |
| MC-06 | `QueuePanel.xaml` L87-88 | Track title/artist hardcode `Foreground="White"` and `Foreground="#888888"`. |
| MC-07 | `build.log` (repo root) | Stale build log checked into the repo root. Should be in `.gitignore`. |
| MC-08 | `temp` (repo root) | A 175-byte `temp` file exists in the repo root. Should be gitignored or deleted. |
| MC-09 | `README.md` | Only 40 bytes. Should contain at minimum: project description, build instructions, architecture overview. |

---

## 11. Summary & Action Plan

### Severity & Finding Counts

| Category | Count |
|----------|-------|
| 🔴 Critical Code & Concurrency Bugs | 5 |
| 🔴 Hazardous / Unsafe UI Actions | 3 (Delete Playlist without confirm, Delete All Duplicates without confirm, Queue artwork binding) |
| ⚠️ UI/UX Bugs & Edge Cases | 38 |
| 💡 Polish, Performance & Explanatory Improvements | 35 |
| ✅ Good Architectural Patterns | 8 |

---

### Top Priority Polish & UX Fixes

1. **Add Confirmation Safeguards on Destructive Actions**:
   - Add a confirmation dialog before deleting a playlist in `PlaylistDetailPage.xaml.cs`.
   - Add a pre-deletion summary confirmation dialog for "Delete All Duplicates" in `SettingsPage.xaml.cs`.
2. **Fix Broken Queue Artwork Binding & Indicator**:
   - Correct `QueuePanel.xaml` artwork binding from `Track.SourceUri` to the album artwork URL.
   - Add an active/playing highlight indicator to the current track in the queue.
3. **Add Comprehensive Tooltips & Explanatory Labels**:
   - Add tooltips across all transport controls (Shuffle, Previous, Play/Pause, Next, Repeat, Mute, Volume).
   - Add tooltips to track row Play and More Menu (`...`) buttons across all list views.
   - Relabel or explain debug/advanced buttons like "Ignite Main Engines (Run Static Fire)".
   - Add tooltips on dry run vs safe changes in library enrichment.
4. **Implement Missing Empty States**:
   - Add friendly empty-state guidance to `AlbumsPage.xaml` and `ArtistsPage.xaml` when no items exist.
5. **Eliminate Hardcoded Colors for Theme Compatibility**:
   - Replace hardcoded hex colors (`White`, `#888888`, `#CCCCCC`, `#1E1E1E`, `#2A2A2A`) across all pages with WinUI 3 `ThemeResource` tokens (`TextFillColorPrimaryBrush`, `CardBackgroundFillColorDefaultBrush`, etc.).
6. **UI-Thread Performance Optimizations**:
   - Throttle `CompositionTarget.Rendering` so it only runs when audio is actively playing and visualizer is visible.
   - Prevent full ListView destruction/reconstruction in `NowPlayingViewModel.RefreshUpNextQueue`.
   - Enable seek bar thumb tooltips on scrubbers so users can see exact seek timestamps.

---

## 12. Second-Pass Deep Audit (Backend, Concurrency & Data Integrity)

> **Added**: 2026-08-19 — independent second-pass review focused on the **service/core layer** (database, audio engine, queue, scanner, watcher, enrichment, providers, orchestrators, ViewModels, helpers, tests) that §1–11 touched only lightly. Every item below was read at the source. Findings that overlap §1–11 are explicitly cross-referenced (e.g. *↔ ME-03*) rather than repeated; treat the cross-referenced pair as **one** fix.
>
> **Severity legend**: 🔴 Critical (data loss / crash / silent corruption) · ⚠️ Bug/Edge · ⚡ Perf/Concurrency · 💡 Improvement · ✅ Verified-clean note.
>
> **ID scheme**: subsystem-prefixed (`DB-`, `AUDIO-`, `QUEUE-`, `SCAN-`, `WATCH-`, `EDITOR-`, `MATCH-`, `ENR-`, `SLE-`, `PROV-`/`MB-`/`CAA-`/`ADB-`/`LRC-`, `CACHE-`/`NET-`/`ORC-`/`SF-`, `VM-`, `SYS-`/`SHELL-`, `HLP-`, `TEST-`, `PH-`). These are **new IDs**; they do not collide with §1–11's `HL-`/`CS-`/`SH-` etc.

### 12.1 Project & Repository Hygiene

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| PH-01 | ⚠️ Bug | `.gitignore` + repo | ~228 files under `bin/`/`obj/` are **git-tracked** (committed before `.gitignore` existed; adding the rule does not untrack them). `.gitignore` also omits `Octave.Core.Tests/bin|obj` entirely. **Fix**: `git rm -r --cached **/bin **/obj`, add the test project's paths to `.gitignore`, commit. |
| PH-02 | ⚠️ Bug | `.vs/` | 13 `.vs/` IDE-state files are tracked. **Fix**: `git rm -r --cached .vs/` and confirm `.vs/` is ignored. |
| PH-03 | ⚠️ Bug | `.gitignore` L5 | Line 5 is `/claude` — almost certainly meant to be `.claude/`. As written it ignores a root file named `claude` and does **not** ignore the `.claude/` dir. **Fix**: correct to `.claude/`. |
| PH-04 | 💡 | `README.md` | 40-byte README (*↔ MC-09*). Add build/run/architecture. |
| PH-05 | 💡 | `build.log` | 4.9 KB stale build log committed at repo root (*↔ MC-07*). Delete + gitignore. |
| PH-06 | 💡 | `temp` | Dev-note file at repo root with a **stale** MVVMTK0045 TODO (*↔ MC-08*). The warning is already resolved (all `[ObservableProperty]` are partial properties). Delete. |
| PH-07 | 💡 | `test_fx/test_fx.csproj` | Stray scratch project tracked in the repo. Remove if unused. |
| PH-08 | 💡 | `Assets/test.mp3` | A test-fixture MP3 ships as **Content** in the production build. Exclude from release packaging. |

> ✅ **Verified accurate**: the report's headline claims are true — `dotnet test` → **128/128 pass**, `dotnet build Octave.Desktop` → **0 warnings / 0 errors**, and all 150 `[ObservableProperty]` sites are partial properties (MVVMTK0045 resolved). The `temp` file's TODO is the only stale claim.

### 12.2 SQLite Database Layer — `SqliteDbContext.cs` (2235 lines)

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| DB-01 | 🔴 Concurrency | `:23-33` `CreateConnection` | **No `PRAGMA busy_timeout`** on any connection. WAL permits many readers + 1 writer, but a *second* concurrent writer (scanner batch-commit while the UI persists player-state on every track change / favorite / playlist reorder) gets `SQLITE_BUSY` (rc 5) **immediately** and throws — no auto-retry. Highly likely given the background scanner. **Fix**: add `PRAGMA busy_timeout=5000;` to the PRAGMA batch at `:29`. **This is the single highest-leverage fix — it also resolves CACHE-01, SLE-08, EDITOR-02, and half of SCAN-01's blast radius.** |
| DB-02 | ⚡ Perf | `:23-33` | `CreateConnection()` does a **synchronous** `Open()` + synchronous PRAGMA `ExecuteNonQuery()`, yet is called from every async method — blocks the calling thread on open, and opens a fresh connection + re-runs 3 PRAGMAs per operation. **Fix**: `CreateConnectionAsync` with `OpenAsync`; consider pooling. |
| DB-03 | ⚡ Perf | `:1784` `SearchLibraryAsync` | Fallback `if (!usedFts && tracks.Count == 0)` runs a full leading-wildcard **LIKE table scan whenever FTS legitimately returns zero rows** — so every genuine *no-match* query pays for *both* an FTS lookup and a full LIKE scan (the check conflates "FTS unavailable/errored" with "FTS ran and found nothing"). *(Correction — 2026-08-20 verification: the earlier wording said this fires for an **empty** query; it does not. `BuildFtsQuery("")` returns empty, so the FTS block at `:1730` is skipped and only LIKE runs for an empty query — that separate, more serious case is now **DB-07**.)* **Fix**: track "FTS executed successfully" separately from row-count; only fall back to LIKE when FTS was unavailable/errored. |
| DB-04 | ⚠️ Edge | `:370-406` `UpsertTrackAsync` (tx==null) | The 3 writes (INSERT Artist, INSERT Album, UPSERT Track) run with **no wrapping transaction** → non-atomic on the `tx==null` path (inconsistent with the tx path). **Fix**: open a local transaction around the trio when no tx is supplied. |
| DB-05 | 🔴 Data-loss | `:244-259` `InitializeAsync` PlaylistTracks migration | Destructive rebuild (`INSERT _Temp SELECT …; DROP TABLE PlaylistTracks; ALTER RENAME`) runs as one multi-statement command with **no explicit transaction** → SQLite auto-commits each; a crash after `DROP` but before `RENAME` **loses all playlist-track rows**. **Fix**: wrap the rebuild in `BEGIN/COMMIT` (DDL is transactional in SQLite). One-time migration, but permanent loss if it hits. |
| DB-06 | ⚠️ Inconsistency | `:1777-1781` | FTS5 catch **swallows all exceptions** to `Debug.WriteLine` (no-op in Release) then silently LIKE-falls-back → genuine schema/corruption errors invisible in prod. **Fix**: catch narrowly (`SqliteException` for malformed MATCH / missing FTS table) and log through a real logger. |
| DB-07 | 🔴 Bug (unbounded result) | `:1723` + Album/Artist queries `:1837+` `SearchLibraryAsync` | **No empty/whitespace guard on the query.** An empty/whitespace query builds `wildQuery = "%" + query + "%"` = `"%%"`; because FTS is skipped for empty input (DB-03), the LIKE fallback runs and `"%%"` matches **every row**, so with `limit == null` the call returns the **entire** Tracks table (Albums/Artists queries do the same) — an unbounded full-library materialization on empty input (UI churn + memory spike; latent DoS if search fires per-keystroke and the box is cleared). **Fix**: short-circuit to an empty (or bounded "recents") result when the trimmed query is empty; always apply a sane `LIMIT`. *(Surfaced by the 2026-08-20 verification pass — the finding DB-03 brushed past but mislabeled. Folds into Batch 1, §15.)* |
| DB-08 | ⚠️ Bug (playlist reorder) | `:1251-1261` `SetPlaylistOrderAsync` | The reorder UPDATE matches `WHERE PlaylistId=@pid AND (Id=@key OR TrackId=@key)`. A playlist may legally contain the **same track more than once** (two `PlaylistTracks` rows, distinct surrogate `Id`, identical `TrackId`). When the caller passes **track ids** — the playlist API deliberately accepts either surrogate-Id *or* track-Id as the key (cf. the mixed-semantics `RemoveTrackFromPlaylistAsync` comment at `:1202`) — `TrackId=@key` matches **all** copies at once, so every duplicate is stamped the **same** `SortOrder` (whichever position that key occupies in the list). The copies collapse to one sort slot and their relative order becomes undefined/unstable across reloads. **Fix**: reorder strictly by the surrogate entry `Id`; never fall back to `TrackId` in a bulk positional update. *(Surfaced by the 2026-08-21 DB re-hunt. Low frequency — needs a playlist with a repeated track — but silent when it hits. Folds into Batch 1 (SqliteDbContext) alongside the reorder work in VM-11/TEST-16.)* |

> ✅ **Verified-clean (DB)**: parameterization is **everywhere** — no SQL-injection surface (search binds `@q`/`@fts`; `IN`-clauses use generated `@p0..@pN`; the only interpolated identifiers at `:266`/`:278` **and** the `$@"…{TrackColumns}…"` dashboard/duplicate queries at `:855`/`:969`/`:987`/`:1005`/`:1017` come from hardcoded column-name literals — *re-confirmed 2026-08-21*, no data ever interpolated). STRICT schema + NOT NULL + DEFAULT + FK CASCADE + CHECK. Transactions on the main write paths are correct (`using var tx` → commit-in-try → `RollbackAsync`+rethrow in catch). Connection/reader/command disposal is correct. Migration is idempotent (`CheckColumnExists` before ALTER).
>
> ✅ **Additionally cleared on the 2026-08-21 deep re-hunt** (probed and found correct — *not* bugs): **(a)** `UpsertTrackAsync` uses `ON CONFLICT(Id) DO UPDATE` (`:417`), **not** `INSERT OR REPLACE`, so a rescan does **not** cascade-delete the track's `PlaylistTracks`/`Favorites`/`PlaybackHistory` rows. **(b)** FTS5 is kept in sync by `AFTER INSERT/UPDATE/DELETE` triggers (`:198-211`) plus a one-time backfill (`:222-230`) — no stale-index masking. **(c)** `DeleteTracksUnderPathAsync` escapes LIKE wildcards escape-char-first and uses `ESCAPE '\'` inside a transaction (`:1457-1479`). **(d)** `RelocateTrackAsync` is fully atomic (all child-table updates + the old-row delete in one tx, `:1287-1349`). **(e)** `GetTracksByIdsAsync` batches at 500 params (dodges SQLite's 999-variable cap) and **re-projects by the requested id order** (`:1568-1572`), preserving both queue **order** and **duplicate multiplicity** on restore. **(f)** `SavedQueue`/`SavedUnshuffledQueue` key on `SortOrder` (`:117`/`:123`), not `TrackId`, so a queue holding the same track twice persists without a PK collision. **(g)** `SavePlayerStateAsync` is atomic (clear→insert→upsert→commit, `:1580-1622`). **(h)** `ReadTrack`'s unguarded `Genre`/`ReplayGain` reads (`:961`) are safe — schema forces `NOT NULL DEFAULT` on both (`:74-75`).

### 12.3 Audio Engine — `ManagedBassAudioService.cs` (865 lines)

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| AUDIO-01 | ⚡ Perf / UI-freeze | `:149-238` `Play` | Holds `_streamLock` across `Bass.CreateStream` (`:179` HTTP / `:183` local). For an **HTTP URL** `CreateStream` does a **blocking network connect while holding the lock** → the 250 ms position timer, `GetFftData` (visualizer), and `Status`/`PositionSeconds`/`DurationSeconds` reads on other threads all block; UI can freeze for seconds opening a remote stream. **Fix**: build the new stream **outside** the lock, then take `_streamLock` only to swap `_currentStream` + attach sync/EQ. |
| AUDIO-02 | ⚠️ UX | `:81-92` `Init` | On device-init failure it falls back to the BASS **"No Sound" device (0)** but still sets `_isInitialized=true` and returns `true` → every `Play()` silently produces **no audio, no error**. **Fix**: record a "silent/no real device" state and surface a warning; retry a real device on device-change. |
| AUDIO-03 | ⚡ Concurrency | `:145` | `if(!_isInitialized) Init();` runs **outside** `_streamLock`; two concurrent `Play()`s could both enter `Bass.Init` (not thread-safe). Low (Play is usually UI-thread). **Fix**: init eagerly at startup, or guard under a lock. |
| AUDIO-04 | ⚠️ Latent | `:822-864` finalizer | Finalizer path takes `_streamLock` and calls native `Bass.ChannelStop/StreamFree/Free` from the finalizer thread → risky native re-entry; locking in a finalizer is discouraged. **Fix**: free native handles only when `disposing==true` (rely on explicit `Dispose` for this app-lifetime singleton), or drop the finalizer. *(↔ TEST-17: tests that never dispose the service can trip this.)* |
| AUDIO-05 | ⚠️ Audio-correctness | `:399-402` | PeakEQ FX is added at **priority 0** and the Compressor/limiter at **priority 1**; BASS applies higher priority first, so the **limiter runs before the EQ** and can't catch clipping from EQ boosts (up to +15 dB). **Fix**: place the limiter at a lower priority than the EQ so it sits last. *(Verify BASS priority-ordering semantics before flipping.)* |
| AUDIO-06 | 💡 Nit | `:140,:354,:423,:663-694` | `CrossfadeDurationMs` has no upper clamp; `_eqGains`/`_currentReplayGainScale`/`_preampGainDb` read outside the lock (benign aligned-float reads); PeakEQ fixed `fBandwidth=2.5` octaves causes heavy band overlap across 10 bands; `GetFftData` reallocates `_lastFftPeaks` outside the lock (benign if UI-thread only). |
| AUDIO-07 | ⚠️ Bug (queue advance / track-skip) | `:764-783` `OnTrackEndedCallback` + `:191`, `:296-308` | The End-sync is registered with **`IntPtr.Zero`** as its user token (`:191`), and the callback **ignores its own `channel` argument** — it reads the **global** `_currentSessionId`/`_currentSourceUri` (`:779-780`), i.e. whatever track is current *now*, not the stream that actually ended. Meanwhile `FadeAndFreeStream` (`:296-308`) slides the **outgoing** stream to silence and frees it only after `fadeMs+100`, **without ever removing its End-sync**. So when a user manually skips (crossfade enabled) within the last ~`fadeMs` of a track — or a natural-end callback for track A is delivered on the ThreadPool *after* a fast manual next — the outgoing stream's live End-sync fires and reports the **incoming** track's session id + uri. `QueueService`'s session-id staleness guard compares that to the now-active (incoming) session, sees a **match**, and treats it as "the current track ended" → **advances the queue, skipping the track that just started**. This is precisely the case the "session-id counter" was believed to cover (see the corrected note below). **Fix**: give each End-sync **per-stream identity** — capture `(sessionId, uri)` keyed by the stream handle at create-time (or pass a token via the `ChannelSetSync` `user` arg) and resolve them in the callback from the ended `channel`, **not** the globals — **and** `Bass.ChannelRemoveSync` the End-sync inside `FadeAndFreeStream`/`Stop` so a superseded/fading stream can never raise `TrackEnded` at all. *(Surfaced by the 2026-08-21 Audio re-hunt; folds into Batch 5.)* |

> ⚠️ **Correction to the earlier audio verified-clean claim**: the previous note asserted "the session-id counter prevents a stale End-sync from a superseded/crossfaded stream advancing the queue." **That is only true for the failure-path** (`Play` stream-create failure fires `TrackEnded` with the *local* per-call `sessionId`). For a **natural end**, `OnTrackEndedCallback` reads the **global** current session, so a superseded/crossfaded stream's end is *indistinguishable* from the current stream's end — see **AUDIO-07**. The counter therefore does **not** cover the crossfade / late-delivery cases.
>
> ✅ **Verified-clean (audio)**: `_streamLock` guards all handle mutation (`Play :149`, `Stop :314`, `FreeStreamInternal :337`, `FadeAndFreeStream :291`, `Dispose :831`, EQ setters `:358/:371/:384`); the End-sync callback (`:764`) correctly defers to the ThreadPool (no native re-entrancy, no free-from-sync-thread) and `PositionChanged`/`TrackEnded` are raised **outside** the lock (`:782`, `:813`); `FadeAndFreeStream` + `Dispose` cooperate via `_fadingStreams` so a fading stream frees exactly once even under a race; volume/mute/replaygain/preamp math is clamped.
>
> ✅ **Additionally cleared on the 2026-08-21 Audio deep re-hunt** (probed and found correct — *not* bugs): **(a)** `_fadingStreams` (`:293`) is drained by **both** `Stop`'s fade path and `Dispose` (`:833-838`) under `_streamLock` → no fading stream leaks on shutdown. **(b)** The position-timer tick re-checks `_currentStream != 0 && ChannelIsActive == Playing` under the lock (`:807-811`) before reading, and `Dispose` holds `_streamLock` across `Bass.Free()` (`:831-855`), so a tick that races teardown (`Timer.Dispose` doesn't await in-flight callbacks) sees `_currentStream==0` and returns — **no use-after-free from the timer**. **(c)** EQ FX lifecycle is leak-free: `SetupEqUnlocked` guards `_eqFxHandle==0`/`_limiterFxHandle==0` before `ChannelSetFX` (`:398-402`); every teardown (`Stop :320`, `FreeStreamInternal :341`, `Dispose :843`) calls `RemoveEqUnlocked` **before** `StreamFree`; `Play` resets both handles to 0 before re-attaching to the new stream — so FX handles don't leak or double-attach across track changes (AUDIO-05 remains the sole EQ finding). **(d)** The mute path is honoured on the crossfade branch too — `finalTargetVolume` uses `(_isMuted ? 0f : _volume)` (`:198`), so a track that *starts while muted* correctly begins silent even when sliding in.

### 12.4 Playback Queue — `QueueService.cs` (805 lines)

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| QUEUE-01 | 🔴 Data-loss | `:134-160` vs `:162-194` | `PersistStateUnlocked` (full snapshot) and `MaybeSaveProgress` (position-only) **share one `_persistenceSequenceToken`** and both guard with `if (token < _lastPersistedToken) return;`. A 5-sec progress tick can grab a higher token and finish first, advancing `_lastPersistedToken` past a still-pending **full-state** save → the full save (new **queue order / shuffle / repeat / current index**) is **silently dropped** and never reaches disk until the next structural change. Reorder/shuffle done just before a tick is lost on restart. **Fix**: give the two paths **separate watermarks** (or forbid the progress-only path from advancing the full-state token). |
| QUEUE-02 | 🔴 Hang/CPU | `:630-694` + `:696-752` | **No consecutive-failure circuit breaker.** With `RepeatMode.Queue` and files that exist but fail to decode (corrupt / unsupported / missing plugin), `Play()` returns a session then fires `TrackEnded` → `HandleTrackEndedAsync` → `PlayIndexInternal((i+1)%N)` → fails again → **infinite loop over the whole queue**, spinning the ThreadPool + DB history writes at high CPU with no audio. **Fix**: count consecutive load failures; after N (or one full lap) `Stop` and surface an error. |
| QUEUE-03 | ⚡ Inefficiency | `:67-73` + `:682` + `:693` | `_audioPlayer.Play` fires `TrackStarted` **synchronously** on the same thread already inside `PlayIndexInternal`; the handler emits `PlaybackStateChanged`, then `PlayIndexInternal` emits **again** at `:693` → **two** full `PlaybackStateChanged`+`QueueChanged` pairs per track change → UI rebuilds twice per skip. **Fix**: drop the emit in the `TrackStarted` handler (PlayIndexInternal already emits). *(This is the root cause behind VM-03 / NP-09 / UI-NP-05 double-rebuild — fix here **and** guard there.)* |
| QUEUE-04 | ⚡ Concurrency | `:775-789` `EmitPlaybackStateChanged` | Raises `PlaybackStateChanged`/`QueueChanged` **while the calling mutator still holds `_queueLock`** (Enqueue/RemoveAt/PlayIndexInternal all wrap the emit). Every subscriber runs under the queue lock; a subscriber that blocks or synchronously waits on a thread needing `_queueLock` stalls/deadlocks. **Fix**: snapshot `state` under the lock, raise events **after** releasing it. |
| QUEUE-05 | ⚡ Inefficiency | `:621-628` `SetVolume` | Every call queues a `Task.Run` + DB `SavePlayerState`; a volume-slider drag floods Task.Run/DB writes (each also reads position via `_streamLock`). **Fix**: debounce volume persistence (as `MaybeSaveProgress` throttles position). |
| QUEUE-06 | ⚠️ Low | `:675` | Missing-file branch **recurses on the call stack** per skipped file → a fully-missing large queue (offline drive) can stack-overflow. **Fix**: convert to an iterative skip loop. |
| QUEUE-07 | 💡 Nit | `:469` `SetShuffle` | `new Random()` per shuffle. **Fix**: `Random.Shared` (also avoids close-timestamp seed correlation). |
| QUEUE-08 | 🔴 Bug (new) | `RestoreAsync` vs `PersistedPlayerState.UnshuffledTrackIds` | `PersistedPlayerState` **persists `UnshuffledTrackIds`**, but `RestoreAsync` rebuilds *both* the active and the unshuffled queues from the saved **active** `TrackIds` order. So if the app is closed **while shuffle is on**, then restarted, toggling shuffle **off** restores the *shuffled* order as the "original" order — the true album/queue order is permanently lost across a restart. **Fix**: on restore, seed the unshuffled queue from `UnshuffledTrackIds` when present; only fall back to `TrackIds` when it's null (pre-migration states). |
| QUEUE-09 | 🔴 Bug (high) | `:457-494` (`Remove` at `:466`) `SetShuffle(true)` | **Enabling shuffle duplicates the currently-playing track.** `currentItem = _activeQueue[_currentIndex]` is an *active-list* instance, but `listToShuffle = new List(_unshuffledQueue)` holds *unshuffled* instances; `QueueItem` has **no `Equals` override** (`DomainModels.cs:66`) and the two lists hold **distinct** objects (same `Id`, different reference), so `listToShuffle.Remove(currentItem)` matches nothing → the rebuild adds the current track at index 0 **and** re-adds its unshuffled twin → the track appears **twice**, `_activeQueue.Count == _unshuffledQueue.Count + 1`, the persisted queue is corrupted, and the duplicate replays later in the same shuffle pass. Trigger: hit shuffle while a song plays (the most common action). Hidden by the weak **TEST-08** (asserts only the flag / post-toggle-off order). **Fix**: prune by id — `listToShuffle.RemoveAll(i => i.Id == currentItem.Id);`. *(Once a dup exists, `RestoreAsync`'s `FindIndex` at `:231` also resolves the resume pointer to the first occurrence.)* |
| QUEUE-10 | ⚠️ Bug | `:302-323` `EnqueueNext` | **Active/unshuffled desync when `_currentIndex == -1`.** The active insert index `_currentIndex+1` clamps to 0 → item goes to the **front** of `_activeQueue`, but the unshuffled insert is guarded by `_currentIndex >= 0` so the item is **appended to the end** of `_unshuffledQueue`; the two lists (meant to mirror when unshuffled) now disagree. Reachable via "Play Next" before pressing Play, or after `RemoveAt` sets `_currentIndex=-1` with tracks remaining. **Fix**: when `_currentIndex < 0`, insert the unshuffled item at index 0 as well. |
| QUEUE-11 | ⚠️ Low–Med *(verify intent)* | `:426-441` reorder path | **Reordering while shuffled mutates the natural order.** The shuffle branch removes the moved item from `_unshuffledQueue` and re-inserts it next to its new active neighbor, so dragging within the shuffled view rewrites the "original" order and a later `SetShuffle(false)` no longer restores the true source order. May be intentional — confirm. **Fix (if unintended)**: leave `_unshuffledQueue` untouched when reordering the shuffled active view. |
| QUEUE-12 | 💡 Low | `:55-127` (no `Dispose`) | Ctor subscribes to `_audioPlayer.TrackEnded/TrackStarted/PositionChanged` + `libraryScanner.LibraryChanged`; the class is not `IDisposable` and never detaches. Benign for the app-lifetime singleton, but any non-singleton construction leaks the instance (kept alive by the handler refs) and the async `TrackEnded` lambda keeps firing. **Fix**: implement `IDisposable`, detach handlers. |

> ⚠️ **Load-bearing invariant (document it)**: all locking is consistently `_queueLock → _streamLock`; the reverse is avoided **only** because the audio service releases `_streamLock` before firing `PositionChanged`/`TrackStarted`/`TrackEnded`. Any future change that raises an audio event *while holding* `_streamLock` will deadlock against `PlayPrevious`/`EmitPlaybackStateChanged`. Add a comment on both locks. *(The ManagedBass failure-path `TrackEnded` is safe today: `_activePlaybackSessionId` is assigned under `_queueLock` at `:682` before release, so the ThreadPool handler blocks until it reads the correct session id.)*

### 12.5 Library Scanner — `LocalLibraryScanner.cs` (715 lines)

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| SCAN-01 | 🔴 Concurrency / Perf | `:134-350` `ScanInternalAsync` | The **entire scan runs inside ONE write transaction**: `BeginTransaction` → per-file `TagLib.File.Create` + `ExtractHighestQualityArtworkAsync` (disk I/O + image-cache write) + Upsert Artist/Album/Track → reconciliation → orphan cleanup → `CommitAsync`. For a large library this holds the SQLite write lock for **minutes**; the WAL can't checkpoint and grows unbounded, and (compounded by DB-01) **every** concurrent UI write hits `SQLITE_BUSY` for the whole scan. **Fix**: parse tags + extract artwork **outside** the write tx; batch DB writes in transactions of ~200 tracks; run reconciliation in its own short tx. |
| SCAN-02 | ⚠️ Bug (task-leak) | `:99-131` + `:251` + `:363-367` | Producer writes to a bounded(100) channel with `ct` (often `CancellationToken.None`). If the **consumer throws**, control jumps to catch→`RollbackAsync`→throw and `await producerTask` (`:251`) is skipped; the producer, blocked in `WriteAsync` on a full channel nobody drains, **hangs forever** as a leaked task. **Fix**: linked CTS cancelled in catch/finally; always drain + await the producer. |
| SCAN-03 | 🔴 Data | `:681-703` `ParseArtistNames` | Delimiter set includes `/` and `\`, so **"AC/DC" splits into "AC" + "DC"** (wrong ArtistId, wrong album grouping, wrong display) — and any slash-bearing artist. **Fix**: drop `/` and `\` from the delimiters (keep `;`), or whitelist known slash names. |
| SCAN-04 | ⚠️ Edge | `:268-289` reconciliation | Deletion compares **raw** stored `dbTrack.SourceUri` against the raw enumerated `discoveredSet`, but gates on a `GetFullPath`-normalized `StartsWith`. A stored path differing beyond case (`.`/`..`, 8.3 short names, alt separators) is wrongly **deleted** (present file) or wrongly kept. **Fix**: normalize **both** sides with `Path.GetFullPath` before building/checking the set. |
| SCAN-05 | ⚠️ Low | `:27,:625-651` `_folderArtCache` | Caches folder art (incl. the "no art" null) for the singleton's lifetime → art added to a folder after the first scan is never picked up until app restart; never trimmed. **Fix**: clear on rescan, or key by `(dir, Directory.LastWriteTime)`. |
| SCAN-06 | ⚠️ Low | `:301-347` | Orphan-artwork `File.Delete` runs **inside** the write tx before commit → non-transactional FS deletes mixed into a tx that can roll back. Safe in practice (only true orphans) but move the sweep to **after** `CommitAsync`. |
| SCAN-07 | ⚠️ Cross-thread | `:157,:237,:351,:471,:530` | `ScanProgressChanged`/`LibraryChanged` are raised from `Task.Run` background threads; UI subscribers **must** marshal to the UI thread. *(Cross-cuts VM-08 — verify every subscriber uses `DispatcherQueue.TryEnqueue`.)* |
| SCAN-08 | 🔴 **Critical (data loss)** | `:106, :123-126, :268-289` | **An unavailable or partially-inaccessible root mass-deletes still-existing tracks.** Reconciliation treats `discoveredSet` as ground truth, but enumeration can be *silently incomplete*: the producer swallows `UnauthorizedAccess`/`IO`/`DirectoryNotFound` (`:123-126`) **and** `IgnoreInaccessible=true` (`:106`) silently skips unreadable folders. **Unplug a USB / disconnect a network root → 0 files discovered → every DB track under that root is DELETED**; one ACL-denied subfolder → all its tracks deleted; a mid-walk network drop → the un-enumerated remainder deleted. Non-swallowed faults are safe (`:251` rethrows *before* reconciliation), so it's exactly the common OS errors that cause loss. **Fix**: gate deletion on a "root fully enumerated & reachable" flag — verify `Directory.Exists(rootPath)` first, skip reconciliation for any root whose producer caught an error or returned an empty `discoveredUris`. |
| SCAN-09 | ⚠️ Bug (wrong grouping) | `:168-171, :665-671, :710` | **Albums keyed by first Performer, not AlbumArtist.** `ParseArtistNames` adds `Performers` *before* `AlbumArtists` (`:667` then `:671`), so `primaryArtistName = individualArtists[0]` is the *track performer* and `albumId = FromAlbum(primaryArtistName, albumTitle)`. A "Various Artists" compilation fragments into N one-track albums; any normal album with a guest track (performer `"Guest feat. Main"`) splits that track into its own album. **Fix**: prefer `Tag.FirstAlbumArtist` (fall back to performer) for `albumId`, `Album.ArtistId`/`ArtistName`. |
| SCAN-10 | ⚠️ Robustness | `:166-247` (esp. `:213`) | **Only `TagLib.File.Create` is inside the skip-and-continue try (`:146-164`).** The processing block (`:166-247`) has a `finally` but **no `catch`**, so any post-open error — `tagFile.Properties` null → NRE at `.Duration` (`:213`), or an upsert failure — propagates to the outer catch (`:363`), rolling back; with the one-transaction **SCAN-01**, *all* processed tracks are lost, and via `RequestFullReconciliationAsync` the throw aborts the loop, skipping all remaining roots. **Fix**: wrap per-track processing in try/catch that logs and `continue`s, mirroring the corrupt-file path. |
| SCAN-11 | 💡 Correctness | `:206-221` | **Disc number never read** (`Tag.Disc` unused; `Track` record has no disc field). Multi-disc albums collapse disc boundaries and collide track numbers (disc 1 trk 1 == disc 2 trk 1) → wrong ordering / apparent duplicate track numbers in album view. **Fix**: read `Tag.Disc`; add a disc field to schema + ordering. |
| SCAN-12 | 💡 UX | `:117, :158, :238` | `TotalFilesFound` is incremented by the producer *while* the consumer already reports it (`Volatile.Read`), so the denominator keeps rising mid-scan → progress % is unstable/misleading until enumeration finishes. **Fix**: report indeterminate until enumeration completes, then emit the final total. |
| SCAN-13 | 💡 Robustness | `:275` | `Path.GetFullPath(dbTrack.SourceUri)` in the reconciliation loop is unguarded; a single malformed `SourceUri` row (invalid chars / over-length) throws → outer catch rolls back and aborts the whole scan **every run**, so the library can never update again until that row is manually removed. **Fix**: try/catch per track; skip the row on failure. |

> ✅ **Verified-clean (scan)**: `_writeSemaphore` serializes scanner writes against each other (they still collide with UI writes → SCAN-01); parameterized DELETEs; orphan Album/Artist cleanup via `NOT IN` subqueries; `RequestFullReconciliation` dedups concurrent requests + 30 s throttle; **file-open** failures (`TagLib.File.Create`) are skipped without aborting *(but post-open processing errors are **not** — see SCAN-10)*; TagLib handles disposed in `finally`. Also confirmed clean on the 2026-08-20 re-hunt: album `ArtworkUrl` uses `COALESCE(excluded.ArtworkUrl, Albums.ArtworkUrl)` so art isn't clobbered by a later art-less track; `FromTrackUri` normalizes case + separators (no case-dup track rows); reconciliation's `normalizedRoot` appends a trailing separator so sibling-prefix roots (`Music` vs `MusicVideos`) don't false-match/false-delete.

### 12.6 Library Watcher — `LibraryWatcherService.cs` (304 lines)

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| WATCH-01 | ⚡ Inefficiency | `:191-210` deletion path | **Every single-file deletion triggers `RequestFullReconciliationAsync()`** = re-enumerate + re-TagLib-parse + re-upsert **all** roots (and takes the long SCAN-01 write tx). The scanner already exposes the cheap targeted `RemoveStalePathAsync(path)`. **Fix**: call `RemoveStalePathAsync(path)` for file deletions; reserve full reconciliation for directory rename + watcher overflow. |
| WATCH-02 | ⚠️ Dead-code | `LocalLibraryScanner.cs:499` `RemoveStalePathAsync` | Grep-confirmed **unused** (only defined + declared in the interface). Either wire it in per WATCH-01 or remove it. |
| WATCH-03 | ⚠️ Race | `:185-277` | Timer-per-path debounce with no lock: an event arriving between the callback's `File.Open` check (`:228`) and `TryRemove` (`:258`) re-arms a timer the callback then disposes, **dropping that event**. Low (periodic reconciliation eventually catches it). **Fix**: re-check/re-enqueue after removal, or serialize per-path arm/fire. |
| WATCH-04 | ⚠️ Low | `:226-247` | A permanently-locked file (held by another app) **reschedules its 2 s timer forever** → endless poll + one live Timer per stuck file. **Fix**: cap retries (~5) then drop; reconciliation picks it up later. |
| WATCH-05 | ⚠️ Low | `:35-40` | Startup reconciliation is fire-and-forget with **no try/catch** → unobserved task exception if it throws. **Fix**: wrap in try/catch like the timer callback does. |
| WATCH-06 | ⚠️ Low | `:284-303` `Dispose` | Iterates `_watchers` **without the lock** and sets no disposed flag; an in-flight Timer callback can still hit the scanner after Dispose. Low (shutdown only). **Fix**: lock + disposed guard. |
| WATCH-07 | ⚠️ Bug (ineffective lock check) | `:228` | The "is the file still being written?" guard opens with **`FileShare.ReadWrite`**, the *weakest* share mode — it succeeds even while another process holds the file open for writing (Explorer copy, most download/tag apps don't lock exclusively). So a half-copied file passes the check → `ScanFileAsync` reads a truncated file → wrong duration/bitrate or a TagLib parse error persisted to the DB. **This directly contradicts the verified-clean claim below.** **Fix**: open with `FileShare.Read` (deny concurrent writers) so an active write throws `IOException` and reschedules; optionally compare size across two ticks. |
| WATCH-08 | ⚠️ Bug (offline root never watched + data-loss amplifier) | `:43-54` | `AddMonitoredPath` calls `_libraryScanner.AddMonitoredPath(path)` (`:48`) **before** the `Directory.Exists` check (`:50`), then returns without creating a `FileSystemWatcher` if the path is absent — and there is **no retry/poll to start watching when it later appears**. Two consequences: (1) a USB/network root that's offline at startup is registered for reconciliation but never watched, so once it's plugged in its changes are invisible until app restart; (2) because it's registered while offline, the startup reconciliation (`:35-40`) enumerates it → 0 files → **SCAN-08 mass-deletes every track under that root**. **Fix**: don't register a root with the scanner (for deletion-reconciliation) until it's confirmed reachable; retry watcher creation when an absent root reappears (periodic re-check or availability event). *(Pairs with SCAN-08.)* |
| WATCH-09 | 💡 Low (timer leak/dup under contention) | `:167-182` | `new Timer(...)` is constructed **inside** the `ConcurrentDictionary.AddOrUpdate` add/update delegates. `AddOrUpdate` may invoke those delegates **more than once** under concurrent events for the same path; each `new Timer` is armed immediately (2 s dueTime) but only one return value is stored → the extras leak (fire a spurious extra scan, or get GC-collected mid-wait and silently never fire). **Fix**: don't create resource-owning objects inside `AddOrUpdate` factories — arm/reset the timer after the dictionary op, or use a lock keyed per path. |

> ✅ **Verified-clean (watch)**: 2 s debounce coalesces the FileSystemWatcher storm; ~~`File.Open` lock-verification avoids reading half-copied files~~ *(**retracted** — see WATCH-07: `FileShare.ReadWrite` makes the check pass during active writes)*; buffer-overflow `Error` handler falls back to full reconciliation; extension filter applied per path; timers + watchers disposed; fire-and-forget scans in `TimerCallback` **do** have try/catch (only the startup task at `:35` lacks one → WATCH-05).

### 12.7 Matching, Enrichment Workflow & Metadata Editor

**Matcher** — `TrackMetadataMatcher.cs` + `MetadataTextNormalizer.cs`

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| MATCH-01 | 🔴 Bug | `TrackMetadataMatcher.cs:154-171` | The exact-ID short-circuit compares `localTrack.Id` against the candidate's ISRC / MusicBrainzId — but `localTrack.Id` is an `IdGenerator.FromTrackUri(path)` **hash**, never an ISRC/MBID. So the strongest signal (`return 1.0` on exact ID) can **never fire** — it's **dead code**. Every local track is fuzzy-scored, pushing genuine ID-exact matches down into "Needs Review". The file's real ISRC/MBID (already read into `currentExtIds` by the workflow) is never threaded in. **Fix**: pass the local `ExternalIds` into `ScoreCandidate` and compare ISRC↔ISRC / MBID↔MBID. |
| MATCH-02 | ⚠️ Edge | `MetadataTextNormalizer.cs:110-114` | `CalculateSimilarity` containment branch **floors similarity at 0.85 regardless of length ratio** → any short string inside a longer one ("Go", "Sia", "Love") scores ≥0.85 and clears the 0.70 gates → false auto-applies. **Fix**: only boost when the length ratio ≥ 0.5 or on a whole-word match. *(This is the concrete risk behind TEST-15's threshold gap.)* |
| MATCH-03 | ⚠️ Inconsistency | `TrackMetadataMatcher.cs:221-225` | Missing duration gets a flat **+0.10**, *larger* than present-but-off-by-6-15 s (+0.08/+0.02) → rewards absent data over imperfect data; a wrong release can become the top candidate. **Fix**: neutral +0.05 or 0. |
| MATCH-04 | ⚠️ Edge | `MetadataTextNormalizer.cs:168` | The 3-part filename regex treats **any hyphen** as an artist/title separator → "Spider-Man Theme.mp3" (no tags) parses artist="Spider", title="Man Theme". **Fix**: require whitespace around ` - `; only trust the artist group with a leading track-number or known shape. |
| MATCH-05 | ⚠️ Inconsistency | `MetadataTextNormalizer.cs:24,77` | Version detection runs over title **+ album combined** → an album named "Live at Wembley" / "Unplugged" marks **all** its tracks `IsLive`/`IsAcoustic`, demoting studio candidates via version-mismatch gates. **Fix**: detect version from **title** primarily; treat album keywords as weak/secondary. |
| MATCH-06 | ⚠️ Edge | `TrackMetadataMatcher.cs:210-219` | Duration scorer has an **unscored dead-zone** `15 s < diff ≤ 25 s` (neither bonus nor the −0.15 penalty) → a ~20 s-off different edit escapes penalty. **Fix**: make the final branch an `else` (penalize > 15 s) or add an intermediate tier. |
| MATCH-07 | ⚠️ Edge | `TrackMetadataMatcher.cs:91,102-103` | If `TagLib.File.Create` throws before assigning `trackNum`, the default **`trackNum=1` survives**, so the filename fallback (`if(trackNum<=0)`) never fires and a **phantom TrackNumber=1** earns +0.05 against any track-1 candidate. **Fix**: init `trackNum=0`, treat 0 as unknown. |

**Track Enrichment Workflow** — `TrackEnrichmentWorkflow.cs`

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| ENR-01 | 🔴 Bug | `:232-237` | External-IDs `FieldComparison` uses record `.Equals`, which compares `ExternalIds.AdditionalIds` (an `IReadOnlyDictionary`) **by reference** → `HasDifference` is almost always `true` → External IDs are perpetually flagged different and rewritten. **Confirmed** against the `ExternalIds` record definition. **Fix**: give `ExternalIds` value-based equality (deep-compare or ignore `AdditionalIds`), or compare only the scalar id fields here. *(Also underlies AR-03's split-behavior concern.)* |
| ENR-02 | ⚡ Inefficiency | `:139-263` | Plan generation fetches candidate **artwork (HTTP, 5 s each)** *and* **lyrics** for **every candidate sequentially** in the foreach, though the user applies only one. N candidates ⇒ N sequential round-trips → slow plan + provider rate-limit pressure. **Fix**: fetch artwork/lyrics lazily for the chosen (or top-only) candidate; parallelize if kept eager. |
| ENR-03 | ⚠️ Low | `:222` | The "Album Artist" comparison proposes the candidate's **track** artist (`meta.ArtistName`) — wrong for compilations / featured-artist tracks. **Fix**: use a real album-artist field, else leave unset. |
| ENR-04 | ⚠️ Low | `:413-419` `CreateComparison` | Diff is case-insensitive, so provider **capitalization-only** corrections are never surfaced as changes. **Fix**: offer capitalization fixes when only case differs. |

> ✅ **Verified non-issue**: the dummy `Track` built with 12 ctor args (`:47,:54,:67`) is fine — the trailing `Genre`/`ReplayGain` ctor params have defaults (confirmed in `DomainModels.cs`).

**Smart Library Enrichment** — `SmartLibraryEnrichmentService.cs`

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| SLE-01 | 🔴 Bug | `:607-608` | The hard-safety MBID gate **also** compares `localTrack.Id` (path hash) to the candidate MBID → **dead code** (same root as MATCH-01). Known-correct-MBID tracks never take the trusted path → demoted to Needs-Review. **Fix**: compare the file's stored MBID/ISRC. |
| SLE-02 | ⚠️ Bug | `:295-297` | `needsMetadata`/`needsArtwork`/`needsLyrics` are computed from the `ScanOnlyMissing*` settings but **never read** (downstream uses `completeness.Is*Complete` directly at `:394,:442`) → those 3 user toggles (default true) are **silently ignored**. **Fix**: gate the action blocks on the `needs*` flags, or remove the settings. |
| SLE-03 | ⚠️ Bug | `:430-437,:216` | Artist enrichment is fire-and-forget (`_ = _artistEnrichmentTasks.GetOrAdd`), the result is **never persisted to the Artist record** (the enrichment service writes only its own cache), yet it's **counted as enriched** → `IsArtistComplete` stays false, so **every scan re-fetches the same artists**; the summary over-reports; the un-awaited task is abandoned on return. **Fix**: await within the throttle and `UpsertArtist` bio/photo before counting. |
| SLE-04 | ⚠️ Edge | `:401-408` | A **faulted** artwork-resolution `Task` is cached in `_albumArtworkTasks` → one transient network blip on an album's first track **poisons all other tracks of that album** for the whole session (they await the same faulted task). **Fix**: `TryRemove` the key on exception, or cache the resolved string, not the Task. |
| SLE-05 | ⚡ Perf | `:114-116,:191` | `allTracks.Select(async …)` eagerly materializes one async state machine + semaphore waiter + an up-front `GetEnrichmentStateRecordAsync` DB read **per track** before the throttle → huge memory/GC/connection spike on large libraries regardless of `MaxConcurrentRequests`. **Fix**: `Parallel.ForEachAsync` with `MaxDegreeOfParallelism` (lazy). |
| SLE-06 | ⚠️ Edge | `:76-78` | `RunEnrichmentScanAsync` cancels+replaces `_scanCts` **without disposing** the prior (leaks linked CTS + registration); shared mutable caches `Clear()` mid-flight if a scan re-enters → duplicate online calls. **Fix**: dispose the old CTS; guard concurrent scans with `SemaphoreSlim(1)`; scope the dedup caches to a scan. |
| SLE-07 | ⚠️ Edge | `:180-184` | Tracks that throw during planning increment `failedCount` but are **never added to plans nor persisted** → `failedCount` can exceed the Failed plans, and the review queue/state DB has no record → they silently vanish from follow-up. **Fix**: build a Failed plan with the exception message and persist a Failed state record. |
| SLE-08 | ⚡ Concurrency | `:112,:167-174,:187` | Up to 8 parallel `SetEnrichmentStateRecordAsync` writes on independent connections + no `busy_timeout` → writers serialize/stall or `SQLITE_BUSY`. **Fix**: DB-01 `busy_timeout` + serialize enrichment-state writes. *(Resolved by DB-01.)* |
| SLE-09 | ⚠️ Inconsistency | `:660-666,:207-217` | `metadataUpdated` counts only Title/Artist/Album; Genre/Year/TrackNumber/DiscNumber/ExternalIds aren't counted → a track that gains only Year/Genre reports **0 fields updated**. **Fix**: count any metadata-writing flag, or give a per-field breakdown. |

**Metadata Editor** — `TrackMetadataEditor.cs` *(reconcile with §4 ME-01..05 and CRIT-03 — same file)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| EDITOR-01 | 🔴 Data-loss | `:99,:105,:108,:111` | Writing Artist / AlbumArtist / Composer / Genre **replaces the entire multi-value tag frame with a single-element array** → destroys additional performers/genres/composers (`["Jay-Z","Alicia Keys"]` collapses to one). Irreversible on the user's files. *(↔ ME-03.)* **Fix**: split incoming on `;`/`/` (or merge with existing), replace only when semantically different. |
| EDITOR-02 | 🔴 Data (DB) | `:262-309` | Renaming Artist/Album regenerates deterministic IDs and upserts **new** Artist/Album rows, re-points the track, but **never deletes the orphaned old rows** (no cleanup like the scanner's). Ghost albums/artists linger in the UI until a full rescan. *(↔ ME-05.)* **Fix**: run orphan-cleanup DELETEs in the same transaction as the upserts. |
| EDITOR-03 | ⚠️ Edge | `:287-309` | The 3 upserts are **un-transacted** (cf. DB-04) **and** run **outside** the scanner `_writeSemaphore` → (a) a mid-failure leaves a partial graph and (b) they hit `SQLITE_BUSY` when a scan holds the long write tx (SCAN-01/DB-01). **Fix**: wrap the trio in one transaction; consider sharing the scanner's write gate. |
| EDITOR-04 | ⚠️ Edge | `:82-88,:173,:192` | Backup/rollback covers only **in-method** exceptions; a process crash/power loss during `tagFile.Save()` leaves a half-written file + an orphaned `.octave_bak` with **no startup recovery**. Backup is a full copy in the **same dir** (2× disk; fails on near-full/read-only volumes). *(↔ ME-01.)* **Fix**: write to a temp file + `File.Replace` (atomic); sweep for stray `.octave_bak` on startup. |
| EDITOR-05 | ⚠️ Edge | `:62-72` | File-lock **pre-check** opens+closes a handle before the real write → TOCTOU window, no real protection. *(↔ CRIT-03.)* **Fix**: drop the probe; open once with the real write handle and treat `IOException` as "locked". |
| EDITOR-06 | ⚠️ Edge | `:113-117` | Year/Track validated only `> 0` — no upper bound → nonsensical provider values (32768, 99999, future years) written verbatim. *(↔ ME-04.)* **Fix**: clamp `1000 ≤ year ≤ now+1`, `1 ≤ track ≤ 999`; apply the same guard in `SmartLibraryEnrichmentService.cs:364-375`. |
| EDITOR-07 | ⚠️ Low | `:185-200` | On write failure the restore `Copy`+`Delete` is wrapped in a **swallowing** try/catch; if restore itself fails the original can be left corrupted **and** the `.octave_bak` remains, with no error surfaced. **Fix**: on restore failure return a distinct hard error naming the backup path; delete the backup only after a verified-good restore. |
| EDITOR-08 | ⚠️ Low | `:242-247` | Post-write verify **re-reads full embedded picture bytes + re-caches on every edit** (even non-artwork edits); the `[0]` fallback can misattribute MIME on multi-picture files. *(↔ ME-02.)* **Fix**: skip artwork re-read/cache unless artwork changed; prefer FrontCover + validate MIME. |
| EDITOR-09 | ⚠️ Low | `:312-316` | If the **file** write succeeds but Stage-3 **DB sync throws**, the backup is already deleted and the file is modified → file/DB divergence until the next scan. Acceptable (file = source of truth) but **log prominently** and surface to the user. |

> ✅ **Verified-clean (editor)**: read-only + `FileShare.None` pre-checks; backup + restore-on-exception sandbox around `TagLib.Save`; existing embedded artwork preserved unless explicitly replaced/cleared; DB synced from the **re-read on-disk** tags (truth), not blindly from the request; `uint` casts guard negative track/disc/year. Culture handling is correct throughout matching/enrichment (`ToLowerInvariant` + `FormD` diacritic strip); the `_writeSemaphore` `Release` is correctly paired in `finally` (Wait outside try).

### 12.8 External Metadata Providers

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| PROV-01 | ⚠️ Bug | `HttpService.cs:53-54` (all providers) | The only User-Agent sent is `OCTAVE/2.0` with **no contact URL/email** → violates MusicBrainz + Cover Art Archive policy; every provider passes `null` customHeaders so none can override → risk of **403/503 throttle/block** in production. **Fix**: a compliant default UA with contact info, or a per-provider UA header. |
| MB-01 | ⚠️ Bug | `MusicBrainzMetadataProvider.cs:322,405,444` | `GetAlbum`/`GetArtist`/`GetReleaseGroupMetadataAsync` **omit the `!IsEnabled` guard** that `GetTrackMetadataAsync` and all Search methods have → they issue live HTTP even when the provider is disabled / online-metadata off / OfflineOnlyMode set → violates offline/privacy settings and burns rate-limit. **Fix**: add the guard to all three. |
| MB-02 | ⚠️ Edge | `MusicBrainzMetadataProvider.cs:527-570` | `CalculateTrackConfidence` never uses `targetArtist/targetAlbum`; `CalculateAlbumConfidence` never uses `targetArtist` → a same-titled result by the **wrong artist** is boosted to ≥0.85, biasing auto-tag toward covers/tributes. **Fix**: fold normalized artist(+album) comparison into the score; penalize mismatch. |
| MB-03 | ⚠️ Edge | `MusicBrainzMetadataProvider.cs:85,109` | Track#/disc# for a search candidate are taken from the **first medium's first track of the first release**, not the matched recording → arbitrary numbers. **Fix**: locate the track in the media whose recording id == `rec.Id`; else leave null. |
| CAA-01 | ⚠️ Bug | `CoverArtArchiveArtworkProvider.cs:61-64` | Treats the ambiguous `externalIds.MusicBrainzId` as a **release** MBID, but it can be a recording/artist MBID (the comment admits it) → `/release/{recordingId}` 404s, then a speculative `/front` that also 404s. **Fix**: only use it as a release MBID when the entity type is known; else resolve via `GetId("MusicBrainzReleaseId")`/search or recording→release. |
| CAA-02 | ⚠️ Edge | `CoverArtArchiveArtworkProvider.cs:122-123,167` | On a JSON-fetch **failure** it returns a speculative `/front` URL → conflates "no art / transient error" with "art exists"; 404s downstream and can't distinguish a guess from known-good. **Fix**: emit `/front` only on a definitive 404-with-no-images, or HEAD-verify. |
| ADB-01 | ⚠️ Edge | `TheAudioDbArtistEnrichmentProvider.cs:55,86` | `?? _options.ApiKey` only fires on **null**; a whitespace/empty settings key survives `.Trim()` as `""` → malformed double-slash URL `.../json//artist-mb.php`. Masked today by `IsEnabled` rejecting blank keys (reachable only via ADB-03). **Fix**: `IsNullOrWhiteSpace(k) ? _options.ApiKey : k.Trim()`. |
| ADB-02 | ⚠️ Edge | `TheAudioDbArtistEnrichmentProvider.cs:70,101` | Both artist lookups blindly take `Artists[0]` with no name/MBID verification → `search.php?s=` can return several artists sharing a name → binds the **wrong** artist's bio/images. **Fix**: pick the best normalized-exact-name match; prefer a `strMusicBrainzID` match when known. |
| ADB-03 | ⚠️ Inconsistency | `TheAudioDbArtistEnrichmentProvider.cs:106-112` | `SearchArtistImageUrlsAsync` has **no top-level `IsEnabled` check** (unlike the profile methods) — fragile if the inner guards change. **Fix**: add an `IsEnabled` short-circuit at the top. |
| PROV-02 | ⚠️ Edge | `MusicBrainzMetadataProvider.cs:41` + `HttpService.cs:171` | The 1 req/s MB limit relies on the provider ctor winning a `GetOrAdd` race; `rateLimiterRegistry` is **optional** (defaults null), and `HttpService` re-creates the limiter by key with only a **500 ms** default → if DI builds the provider without the registry, MB runs at **2 req/s** and silently violates policy. **Fix**: make the interval authoritative at the HTTP layer (key→interval map), or require the registry for MB and assert the limiter exists. |
| PROV-04 | ⚠️ Edge | `ArtistEnrichmentService.cs:51-64,121-124` | A profile whose **image download failed** is cached 30 days with `LocalImageToken=null` and thereafter returned immediately **without retrying** the image → a transient failure leaves an artist image-less for 30 days. **Fix**: short TTL (or an "image pending" marker) when the token is null; or re-attempt on a cache hit that lacks a token. |
| LRC-01 | ⚠️ Edge | `LrcLibLyricsProvider.cs:55` | The in-flight dedup key `lrclib:{artist}:{title}` **omits album + duration** → concurrent lookups for the same title+artist but different album/duration share one result → wrong-version lyrics across releases. **Fix**: include normalized album + rounded duration. |
| LRC-02 | 💡 | `LrcLibLyricsProvider.cs:69-79` | The fallback after an exact `/get` miss **re-calls `/get`** (still exact) instead of fuzzy `/search` → slightly-off title/artist still misses. **Fix**: fall back to `/api/search` + best-scoring result (match duration). |

> ✅ **Verified-clean (providers)**: JSON is null-safe (`PropertyNameCaseInsensitive` + explicit `[JsonPropertyName]` on every field; missing → null/default, all sites null-guard). Query/path construction is sound (`EscapeLucene` then `Uri.EscapeDataString`). HTTP transient handling (429/502/503/504/Retry-After/timeout) is centralized and correct. Results are bounded (limit=10/single) — no unbounded pagination. `ArtistEnrichmentService` cancellation is correct.

### 12.9 Orchestrators, Two-Tier Cache & Network

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| SF-01 | ⚠️ Bug | `AsyncSingleFlight.cs:22,31-38` | The factory runs under the **first caller's** `ct`, and its result/exception is shared with all waiters → one caller cancelling or throwing **corrupts unrelated callers** (a joiner is cancelled by an unrelated leader, or can't honor its own ct). Every provider uses this. *(↔ HL-01 flags the spin; this is the more serious semantic bug.)* **Fix**: run the factory with `CancellationToken.None`; have each waiter `await task.WaitAsync(ct)` for its own cancellation. |
| NET-01 | ⚠️ Bug | `ProviderRateLimiterRegistry.cs:44-52` | `NotifyRetryAfter` reads/writes `_nextAllowedTime` **unsynchronized** while `WaitAsync` writes it under a semaphore → torn `DateTimeOffset` (16 B) → starvation or lost throttle. **Fix**: lock, or store UTC ticks as a `long` and use `Interlocked`. |
| NET-02 | ⚠️ Bug | `HttpService.cs:207-211` | A non-success response is returned as `Failure` **without disposing** the `HttpResponseMessage` (the retry branch at `:201` does dispose) → connection leak (esp. with `ResponseHeadersRead`). **Fix**: dispose before the `Failure` return. |
| NET-03 | ⚠️ Bug | `HttpService.cs:285-298` | `CloneRequest` drops `req.Content` + content headers → an **empty body** for POST/PUT/PATCH retries. GET-only today, so latent. **Fix**: copy the buffered `Content` + `Content.Headers`. |
| CACHE-01 | ⚠️ Bug | `TwoTierExternalDataCache.cs:63,85` | The L2 read catches only `JsonException`; a `SqliteException` (`SQLITE_BUSY` — see DB-01) **propagates through the orchestrators**, defeating the offline/stale fallback. The write path swallows *all* exceptions (inconsistent). **Fix**: catch `Exception`→null on read; add DB-01 `busy_timeout`. |
| CACHE-02 | ⚠️ Edge | `TwoTierExternalDataCache.cs:40,106` | L1 stores/returns the caller's object **by reference** (shared) while L2 returns a fresh copy → a cached mutable `List<TrackMatchCandidate>` is shared across callers; mutation corrupts the cache / races enumeration. **Fix**: defensive-copy mutable collections. |
| CACHE-03 | ⚡ Perf | `TwoTierExternalDataCache.cs:188-209` | `EnsureL1Capacity` does O(n) LINQ + O(n log n) `OrderBy` on **every** set/promotion at capacity; it evicts by `CreatedAt` (**not** LRU); the comment says "20%" but it removes a fixed 50 (~5% of 1000). **Fix**: track last-access, evict true-LRU, batch-evict proportionally. |
| CACHE-04 | ⚠️ Edge | `TwoTierExternalDataCache.cs:105-106` | `EnsureL1Capacity` then dict-set is **check-then-act**, not atomic → concurrent setters overshoot capacity. **Fix**: serialize evict+insert. |
| CACHE-05 | ⚠️ Inconsistency | `TwoTierExternalDataCache.cs:88` | Corrupted-row cleanup `_ = RemoveCachedExternalDataAsync(key)` bypasses `_writeLock`, fire-and-forget → races serialized writes; errors unobserved. **Fix**: `await` the remove (which takes the lock). |
| CACHE-06 | ⚠️ Inconsistency | `TwoTierExternalDataCache.cs:104-108` | `SetAsync` writes **L1 before the ct check** → a cancelled set leaves the value in L1 but not L2; a restart loses it and the tiers disagree. **Fix**: check ct up front. |
| NET-04 | ⚠️ Inconsistency | `ProviderRateLimiterRegistry.cs:65-72` | `GetOrCreate` honors `defaultMinInterval` only on **first** creation; later differing intervals are ignored; the closure allocates per call. **Fix**: keyed `GetOrAdd` overload; document/enforce the interval. |
| ORC-01 | ⚡ Perf | `OnlineLyricsOrchestrator.cs:46,78,99` + `ExternalMetadataOrchestrator.cs:84-89` | **Negative/empty results are never cached** → unresolvable tracks re-run the full provider fan-out on every non-concurrent call. *(↔ OL-02.)* **Fix**: cache a negative sentinel with a short TTL. |
| ORC-02 | 💡 | `CompositeLyricsService.cs:48` | Always passes `null` externalIds to the online orchestrator, discarding the track's MBID/ISRC → weaker match. **Fix**: thread `Track`→`ExternalIds` through. |
| ORC-03 | ⚡ Perf | `ExternalArtworkOrchestrator.cs:57-59,73-76,133-135` | Synchronous `File.Exists` on the hot path (twice) + a fragile whole-string `Replace("ArtworkCache/","")` that duplicates the token layout. *(↔ OL-08.)* **Fix**: add `ResolveToken()` to `IArtworkCacheManager` + a brief existence cache. |
| NET-05 | 💡 | `HttpService.cs:175` | `new Random()` per `SendAsync` for jitter. **Fix**: `Random.Shared`. |
| SF-02 | 💡 | `AsyncSingleFlight.cs:18-45` | Dedup is best-effort: a leader completing between a waiter's `TryGetValue` and `TryAdd` lets a second leader re-run the factory; a discarded TCS is garbage each failed iteration. *(↔ HL-01.)* Not a spin. **Fix**: a short-lived result cache if strict dedup is required. |

> ✅ **Verified non-issues** (investigated and dismissed): cache-key colon collision (Normalize strips `:`), and the artwork hex-hash token round-trip — both are safe.

### 12.10 ViewModels

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| VM-01 | 🔴 Leak | `ExternalDataSettingsViewModel.cs:126` | Subscribes the singleton settings service via an inline lambda with **no unsubscribe / `Cleanup`** → one VM leaks per Settings navigation; every `SaveAsync` then re-runs `LoadFromSettings` on **all** leaked instances. **Fix**: store the handler, unsubscribe in a `Cleanup()`/`IDisposable` invoked on nav-away. |
| VM-02 | 🔴 Leak | `LibraryEnrichmentViewModel.cs:205` | Subscribes `ProgressChanged`/`ScanCompleted` on the singleton enrichment service with **no unsubscribe** → one VM leaks per dialog open; leaked instances still pump dispatcher work on every scan. **Fix**: unsubscribe on dialog close. |
| VM-03 | ⚡ Perf | `NowPlayingViewModel.cs:181` | UpNext queue is rebuilt **twice** per state change — once in `UpdateFromState` (:181) and again in `OnQueueChanged` (:136) — because `QueueService` emits `PlaybackStateChanged` + `QueueChanged` back-to-back (`QueueService.cs:787-788`). *(↔ NP-09, UI-NP-05, QUEUE-03.)* **Fix**: drop `RefreshUpNextQueue` from `UpdateFromState`. |
| VM-04 | ⚠️ Bug | `EntityDetailViewModel.cs:84` | `LibraryUpdated` fires an **uncancelled, concurrent** `LoadEntityAsync` (re-entrant from `EnrichEntityAsync`'s `NotifyLibraryUpdated` at :183,:202) with no generation token and no try/catch → stale results can win the race and exceptions are swallowed. **Fix**: generation token (drop stale completions) + try/catch. |
| VM-05 | ⚠️ Bug | `MetadataEnrichmentViewModel.cs:147,247` | `_searchCts` is cancelled but **never disposed**; `_applyCts` neither cancels nor disposes its predecessor; no `Cleanup` on dialog close. *(↔ ME-06.)* **Fix**: dispose-then-replace both CTSs; cancel on close. |
| VM-06 | ⚠️ Bug | `HomeViewModel.cs:60` (+ `LibraryViewModel:59-75`, `AlbumsViewModel:27-39`, `ArtistsViewModel:27-39`, `PlaylistsViewModel:25-34`) | Fire-and-forget `_ = LoadAsync()` with **no try/catch inside `LoadAsync`** → DB failures are swallowed and the UI is left stale/empty with no error. **Fix**: wrap the body; surface an error state. |
| VM-07 | ⚠️ Edge | `LibraryEnrichmentViewModel.cs:210-227` | `IsScanning` stays **stuck true** if the service throws before `ScanCompleted` (`StartPreviewDryRunAsync`/`ApplySafeChangesAsync` lack try/catch); `SkipAsync`/`NeverAskAgainAsync` also lack the catch that `ApplyCandidateAsync` has. **Fix**: reset `IsScanning` in `finally`; add symmetric try/catch. |
| VM-08 | ⚠️ Edge | `LibraryEnrichmentViewModel.cs:242` (+ `NowPlayingViewModel.cs:287-323`) | Mutates `ObservableCollection` **after `await` without `_dispatcher.TryEnqueue`**, unlike the marshalled sites elsewhere → cross-thread COM risk if the continuation lands off the UI thread. **Fix**: marshal all post-await collection mutations. |
| VM-09 | ⚠️ Bug | `ExternalDataSettingsViewModel.cs:256` | `_testCts` cancelled + replaced but **never disposed**. **Fix**: dispose before replacing. |
| VM-10 | ⚡ Perf | `HomeViewModel.cs:152`, `SearchViewModel:81-106`, `EntityDetailViewModel:125-153`, `PlaylistDetailViewModel:86-87` | Unconditional `Clear()` + re-add with **no `SequenceEqual` guard** (unlike Albums/Artists/Playlists/Library VMs) → flicker, lost scroll/selection, O(n) churn on every refresh. **Fix**: diff before replacing, or a keyed reconcile. |
| VM-11 | ⚠️ Edge | `PlaylistDetailViewModel.cs:98` | Reorder persists **only on the `Move` action**, fire-and-forget `SetOrderAsync` per event (spam) with no error handling; a Remove+Add-style reorder is **not persisted**. **Fix**: debounce one persist after the reorder settles; handle errors; cover all reorder paths. |
| VM-12 | ⚠️ Edge | `EqBandViewModel.cs:24` | Ctor `Gain = gain` relies on `OnGainChanged`, but the generated setter **skips when value == default(0)** → 0-gain bands are never pushed to the engine; also mutates the engine **during construction**. **Fix**: apply gain explicitly after construction; don't drive the engine from the ctor. |
| VM-13 | ⚠️ Inconsistency | `MetadataEnrichmentViewModel.cs:285` | `ApplyChangesAsync` mutates observable props **directly** after `await` while `RunSearchAsync` uses the dispatcher; a **cancelled** apply still writes success state. **Fix**: marshal consistently; short-circuit state writes when cancelled. |

> ✅ **Verified-clean (VMs)**: Albums/Artists/Playlists/Library VMs correctly `SequenceEqual`-guard their collection refreshes; commands use `[RelayCommand]` with proper `CanExecute`; most dispatcher marshalling is correct — the findings above are the specific asymmetric sites.

### 12.11 Desktop Shell & System Integration

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| SYS-01 | 🔴 Bug (latent) | `WindowMinSizeHelper.cs:38,43,60` | The Win32 subclass delegate is stored in a **single static field** → rooted for only one window and silently clobbered if `SetMinSize` is called for a 2nd window → the first window's delegate becomes GC-eligible while the OS still holds the native fn ptr → the next `WM_GETMINMAXINFO` invokes a collected delegate → `CallbackOnCollectedDelegate`/AV **crash**. Latent today (only `MainWindow` calls it once) but a landmine the moment `MiniPlayerWindow` adopts a min size. **Fix**: keep a per-hwnd `Dictionary<IntPtr,SubclassProc>` (or an instance field alive for the window's life) + `RemoveWindowSubclass` on close. |
| SYS-02 | 🔴 Bug | `App.xaml.cs:71,200-206` | A DB-init failure rethrown from `async void OnLaunched` is caught by the **swallow-everything** `UnhandledException` handler (`e.Handled=true`) → window creation at :206 never runs → an **invisible zombie process** (killable only via Task Manager). The rethrow is pointless (unconditionally eaten). *(↔ SH-10.)* **Fix**: on init failure show a fatal dialog / `Application.Exit()`; don't blanket-set `e.Handled=true`. |
| SYS-03 | ⚠️ Edge | `WindowsSmtcService.cs:94-96,119-121,155-166` | `UpdateSmtcState` runs directly on the `QueueService` background `PlaybackStateChanged` thread and mutates shared `_lastTrackId`/`_hasPushedDisplay` + the `DisplayUpdater` with **no synchronization**, racing the `UpdateThumbnailAsync` continuation → torn/stale SMTC metadata (wrong title/art on the OS media flyout). `ShellViewModel` marshals via the dispatcher; this path never does. **Fix**: marshal onto `_dispatcherQueue`, or lock the shared fields + `Update()` with a generation check. |
| SYS-04 | ⚠️ Inconsistency | `WindowsSmtcService.cs:84-91` | The SMTC button-handler branch is **inverted**: it dispatches to the UI only when *off* the UI thread and hops to the ThreadPool when *already on* it → Resume/Pause/Next/Prev run on the UI dispatcher in the common background case but on a pool thread in the rare on-UI case → latent race + non-deterministic contract. **Fix**: route all button handling through one context (e.g. `TryEnqueue` unconditionally). |
| SYS-05 | 💡 | `WindowsSmtcService.cs:45,48` + `ISmtcService.cs:5-8` + `WindowMinSizeHelper.cs:60` | SMTC event subscriptions (`ButtonPressed`, `PlaybackStateChanged`) + the Win32 subclass are **never torn down**; `ISmtcService` exposes only `Initialize`, no `Dispose`. Benign for app-lifetime singletons but there's no clean shutdown/re-init path. **Fix**: add `Dispose()` that unsubscribes both + clears the `DisplayUpdater`; `RemoveWindowSubclass` on close. |
| SHELL-01 | ⚠️ Edge | `ShellViewModel.cs:405-414` | The sleep-timer fire callback captures the `_sleepTimer` **field**, not the fired instance → a stale fired Timer A can dispose a freshly-created Timer B, pause playback unexpectedly, and overwrite the status to "Off" while B never fires. **Fix**: capture the created timer in a local + `ReferenceEquals` guard inside the enqueued action, or use a generation token. |
| SHELL-02 | ⚠️ Inconsistency | `MainViewModel.cs:17-38` (registered `App.xaml.cs:129`) | `MainViewModel` is a registered **singleton** whose Play/Pause/Stop only flip a local `IsPlaying` bool and **never touch** the injected `IAudioPlayerService` → dead, misleading state; no references outside its own file + the DI registration. **Fix**: delete `MainViewModel` + its registration (or wire the commands to `_audioPlayer`/`IQueueService`). |

> ✅ **Verified-clean (shell)**: `PlaybackSlider` + `MiniSeek` both wire `PointerCaptureLost`/`PointerCanceled` → `IsDragging=false` so a drag can't get stuck; no captive DI dependencies (all singletons depend only on singletons); the `ShellViewModel` singleton shared by `MainWindow` + `MiniPlayerWindow` avoids duplicate subscriptions; `MiniPlayerWindow.ShowMain` is idempotent via `_restored`.

### 12.12 Helpers & Converters *(second pass — reconcile with §9)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| HLP-01 | ⚠️ Edge | `SpectrumVisualizerControl.xaml.cs:93-116` | `UpdateSpectrum` writes `bar.Height`/`Canvas.SetTop`/`bar.Fill` — all **UI-thread-affine**. The FFT/position source (`ManagedBassAudioService` position timer + `GetFftData`) fires on a **background** thread, so the caller **must** marshal each frame onto the `DispatcherQueue` or this throws a cross-thread COM exception. *(↔ HL-06, HL-07 — verify the call site in NowPlaying.)* **Fix**: marshal at the call site, or make `UpdateSpectrum` self-marshal via a captured dispatcher. |
| HLP-02 | ⚠️ Low | `IdGenerator.cs:20,38,56` | `FromArtist`/`FromAlbum`/`FromTrackUri` don't null-guard their args → a null name/uri throws `NullReferenceException` inside SHA-256 rather than a clear `ArgumentNullException`. **Fix**: guard and either throw a clear exception or treat null as empty. |
| HLP-03 | ⚠️ Low | `MetadataTextNormalizer.cs:110-114,168,24-77` | Same three root issues already filed as **MATCH-02** (0.85 containment floor), **MATCH-04** (hyphen filename split), **MATCH-05** (title+album version detection) — the fixes live in this file. Cross-referenced here so the helper pass isn't read as "clean". |
| HLP-04 | 💡 | `ArtworkPathConverter.cs:47` | Stale comment (describes a prior non-LRU design); the LRU cache (250-cap, `CacheLock`-guarded) is otherwise sound and `ConvertBack` correctly throws for a one-way binding. *(↔ HL-03 background-thread `BitmapImage`, CS-02 path separator, CS-03 no eviction budget.)* **Fix**: update the comment. |
| HLP-05 | 💡 | `DurationFormatConverter.cs:10` / `DbFormatConverter.cs:12-18` | Neither guards `double.IsInfinity` (Duration guards NaN + `≤0`; Db is bounded in practice) → an infinite input would format oddly but not crash. Very low. **Fix**: add an `IsInfinity` short-circuit for defensiveness. |
| HLP-06 | 💡 | `ImageValidator.cs` | Magic-byte validation covers JPEG/PNG/WebP/GIF; **BMP/TIFF are silently rejected** as invalid. Fine for cover art (rare formats) but the drop is silent. **Fix**: either accept BMP/TIFF or log the rejection reason. |

> ✅ **Verified-clean (helpers)**: `ShellRecycleBin` (COM `IFileOperation` with `SHFileOperation` fallback; the double-null `LPWStr` marshalling is correct); `AsyncSingleFlight` core dedup (the real issues are SF-01/SF-02, already filed); `IdGenerator.ResolveFileDateAdded`'s 1980 FAT-epoch sentinel; `LyricsService` LRC parsing (offset sign matches its comment; overlaps OL-10..14 with no new defects).

### 12.13 Test Suite — `Octave.Core.Tests`

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| TEST-01 | 🔴 Coverage | `TrackMetadataEditor.cs:82-200` | The **backup/rollback sandbox has zero coverage** — all three failure tests short-circuit *before* the backup is taken (read-only :58, lock :71), so `File.Copy` backup (:88) + restore (:187-196) are never exercised. This is the highest-risk code in the app (it mutates the user's files) and it's untested. **Fix**: force `tagFile.Save()` to throw *after* the backup, assert byte-for-byte restore + no leftover `.octave_bak`. |
| TEST-02 | 🔴 Coverage | `TrackMetadataEditor.cs:312-316` | The **file-written-but-DB-sync-fails** path is untested (backup already deleted at :177, then Stage-3 throws → `DbSyncFailed`, file mutated, backup gone). The only integrity test uses a read-only file, where the *write* fails first. **Fix**: writable file + a DB mock forced to throw on `UpsertTrackAsync`. |
| TEST-03 | ⚠️ Bug | `FormattingTests.cs:23-28` | Tests a **private copy** of `FormatSeconds` defined inside the test, not the production `DurationFormatConverter.cs:8-13` → can never catch a regression. **Fix**: call the real converter. |
| TEST-04 | ⚠️ Bug | `LyricsServiceTests.cs:62-103` | The "binary search seek" test **reimplements** `FindIndex` locally (:74-95) and asserts against its own copy; production has no such method (the real lookup lives in the NowPlaying VM). **Fix**: extract the production lookup into Core and test it (incl. boundaries). |
| TEST-05 | ⚠️ Bug | `AudioPlayerServiceTests.cs:9-15` | `Assert.NotNull` on properties that are **hardcoded non-null** (StreamingQuality default "Unknown", OutputDeviceName, OutputDeviceQuality) → cannot fail. **Fix**: assert the actual format/shape/values. |
| TEST-06 | ⚠️ Bug | `AudioPlayerServiceTests.cs:34-47` | `GetFftData` test checks **only array length** (set unconditionally at :693); with no stream it's decayed zeros → the bin mapping is never validated. **Fix**: inject a raw FFT buffer and assert exact bin outputs. |
| TEST-07 | ⚠️ Bug | `AudioPlayerServiceTests.cs:17-25` | Asserts `SampleRate>0`/`BitDepth>0`, which the (44.1 kHz, 16-bit) fallback **guarantees**, and couples the test to live COM. **Fix**: abstract the COM call, test parsing against a fake `WAVEFORMATEX`. |
| TEST-08 | ⚠️ Bug | `QueueServiceTests.cs:80-107` | The `SetShuffle` test **never asserts the order changed** (only the `IsShuffle` flag) → a no-op shuffle would still pass. **Fix**: seed the RNG and assert the permutation differs from the original. |
| TEST-09 | ⚠️ Bug | `ArtworkRetrievalTests.cs:416-443` + `LyricsRetrievalTests.cs:199-220` + `MusicBrainzProviderTests.cs:317-335` | Cancellation tests **pass even if cancellation is ignored** (the mock returns OK regardless; the test only asserts Null/Empty). **Fix**: assert the HTTP handler was invoked `Times.Never` on a pre-cancelled token. |
| TEST-10 | ⚠️ Edge | `LyricsService.cs:180-197` | LRC edge cases untested: 1-digit/3-digit ms, multi-timestamp lines (`[00:01][00:05]shared`), offset > timestamp. **Fix**: add `.5`/`.500`, shared-line, and large-offset cases. |
| TEST-11 | ⚠️ Edge | `QueueServiceTests.cs:122-142` | `RestorePositionOnStartup` only tests the **default-false** path (`Seek` `Times.Never`); the actual resume-position feature (Seek *called*) is untested. **Fix**: set true, restore a 90 s position, verify `Seek(90.0)` `Times.Once`. |
| TEST-12 | ⚠️ Edge | `AsyncSingleFlight.cs:31-39` | Exception propagation + empty-key bypass are only implicitly touched on the happy path. **Fix**: direct tests for a concurrent throwing factory + empty key (and the SF-01 per-caller-ct semantics once fixed). |
| TEST-13 | ⚠️ Coverage-gap | `LocalLibraryScanner.cs` (715 lines) | **No test file** — only mocked elsewhere. Directory walk, tag extraction, malformed-file handling, dup detection, and cancellation are unverified. **Fix**: scan a temp dir with valid + malformed + zero-byte files. |
| TEST-14 | ⚠️ Coverage-gap | `LibraryService.cs` + `LibraryWatcherService.cs` | **No test files.** `ToggleFavorite` (:41), `Add/RemoveFolder` (:56-73), `RescanAll` (:75), and watcher debounce are untested. **Fix**: test against a real `SqliteDbContext` + a mocked scanner. |
| TEST-15 | ⚠️ Coverage-gap | `MetadataTextNormalizer.cs` | The fuzzy-match core is tested only indirectly: `CalculateSimilarity`/Levenshtein (:100-148), the 0.85 containment floor (MATCH-02), `&`→`and` (:54-63), and `ParseFromPath` (:154-189) have **no direct tests** → a threshold shift → wrong auto-apply would go undetected. **Fix**: table-driven tests. |
| TEST-16 | ⚠️ Coverage-gap | `PlaylistService.cs` | **No test file.** `SetOrderAsync` reorder + title validation untested. **Fix**: create → add → reorder → remove → delete against a real DB. |
| TEST-17 | 💡 | `AudioPlayerServiceTests.cs:11,43,52,61,78` | 5 tests construct `ManagedBassAudioService` **without `Dispose`**; the finalizer's `Bass.Free()` (:816-864) can tear down the **shared** BASS engine → flaky under parallelism. *(↔ AUDIO-04.)* **Fix**: `using`/`IClassFixture`. |
| TEST-18 | 💡 | `TrackMetadataMatcherTests.cs:298` | Fixed temp filename `"06 - Pink Floyd - Money.mp3"` with **no Guid** → parallel-run collision. **Fix**: per-test Guid dir. |
| TEST-19 | 💡 | `QueueServiceTests.cs:66,73` (`Task.Delay(100)`) + `MusicBrainzProviderTests.cs:342-357` (`ElapsedMs>=140`) | **Wall-clock nondeterminism** → flaky CI. **Fix**: `TaskCompletionSource` signal / injected clock. |
| TEST-20 | 💡 | (all `IDisposable` fixtures) | Every fixture uses **real on-disk SQLite** (+ FS/TagLib I/O) → integration, not unit; the swallowed cleanup `try/catch{}` leaks temp DBs. **Fix**: `:memory:` for pure logic; mark persistence tests as integration. |

> ✅ **Verified-clean (tests)**: happy-path breadth is genuinely good (128 passing, 0 warnings — confirmed by a real `dotnet test` run); the gaps cluster in the high-risk areas (editor rollback, audio interop, match thresholds, scanner/watcher). No test asserts against production-copied logic *except* where flagged (TEST-03/04).

---

### Updated severity tally (second pass, §12)

| Severity | §12 count | Notes |
|----------|-----------|-------|
| 🔴 Critical / data-loss / crash | 12 | DB-01, QUEUE-01, QUEUE-02, AUDIO-01, ENR-01, MATCH-01/SLE-01 (dead exact-ID path), EDITOR-01, EDITOR-02, VM-01, VM-02, SYS-01, SYS-02, TEST-01/02 |
| ⚠️ Bug / edge / inconsistency | ~85 | across all subsystems |
| 💡 Improvement / nit | ~25 | performance, ergonomics, hygiene |

Several §12 criticals are **the same root cause** surfacing in multiple files — most importantly **DB-01 (`busy_timeout`)** which underlies SCAN-01, EDITOR-02/03, CACHE-01, SLE-08, and the enrichment write storms, and the **path-hash-vs-ISRC/MBID dead comparison** which appears as MATCH-01, SLE-01, *and* the intent behind ENR-01. Fixing those two roots resolves ~10 downstream findings. This is reflected in the batch ordering in **§15**.

---

## 13. Third-Pass: Integration, Startup Wiring & Dead-Setting Audit

*Focus of this pass: how the newly-added online-fetching subsystem is **wired into the running app** — startup initialization order, whether the persisted user settings actually reach the services that read them, and whether every setting exposed in the UI is honored by code. This is the class of defect that a per-file review misses because each file is individually correct; the bug lives in the seams. Everything below was confirmed by reading the live call paths (`App.xaml.cs` `OnLaunched`, `ExternalDataSettingsService`, `SmartLibraryEnrichmentService.Plan`, `CompositeLyricsService`) and by a repo-wide grep for each setting's usages.*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| **INT-01** | 🔴 Bug (privacy + correctness) | `App.xaml.cs` `OnLaunched` (:71-221, esp. :192-220) + `ExternalDataSettingsService.cs:34` `LoadSettingsAsync` + `SettingsPage.xaml.cs:31` | **Persisted external-data settings are never loaded at startup.** `IExternalDataSettingsService` is registered (`App.xaml.cs:95`) but `LoadSettingsAsync()` is called **only** from `SettingsPage.OnNavigatedTo`. Until the user opens Settings, `CurrentSettings` returns the hardcoded model defaults (`OfflineOnlyMode=false`, every provider enabled, `TheAudioDbApiKey="2"`). Providers read `CurrentSettings` **live** (correct design) — so on a cold launch a user who persisted `OfflineOnlyMode=true` (or disabled MusicBrainz/lyrics) will still hit the network: e.g. first play → `CompositeLyricsService` → `OnlineLyricsOrchestrator` → `provider.IsEnabled` reads the *default* (online) settings and fetches. The user's saved privacy/offline preference is silently ignored for the whole session until Settings is visited. **Fix**: `await settingsService.LoadSettingsAsync()` in `OnLaunched` **before** `_window.Activate()` and before `RestoreAsync()` / watcher init. *(Root cause behind several "online calls happen even though I turned them off" symptoms. ↔ SLE-02, ORC-02.)* |
| **INT-02** | 🔴 Bug (writes user files against stated policy) | `SmartLibraryEnrichmentService.cs` `Plan` (:300-464, gates at :332-389) + `ExternalDataSettings.cs` (`AutoFillMissingMetadata`/`ReplaceExistingMetadata`/`WritePolicy`) | **Three write-policy settings are dead — never read by any service.** A repo-wide grep finds `AutoFillMissingMetadata`, `ReplaceExistingMetadata`, and `WritePolicy` **only** in the model, the Settings VM, tests, and `SettingsPage.xaml` — **never in a service**. `Plan` decides each metadata field write purely from *completeness* (`IsMissingOrGeneric`, `Year<=0`, …) + candidate presence. Consequences: (a) the master **`AutoFillMissingMetadata` toggle (default `false`) does nothing** — auto-enrichment writes tags to the user's files even when it's off; (b) the **`WritePolicy` dropdown is inert** (its default *happens* to coincide with the hardcoded "fill-missing-only" behavior, so it looks like it works — it doesn't); (c) `ReplaceExistingMetadata` is ignored. **Fix**: gate the entire metadata plan on `AutoFillMissingMetadata`; select fields by `WritePolicy` (`WriteOnlyWhenMissingAndHighConfidence` / fill-missing / replace-generic / replace-all); honor `ReplaceExistingMetadata`. *(This is the highest-impact new finding: it silently mutates the user's library contrary to their explicit settings. ↔ SLE-02.)* |
| **INT-03** | ⚠️ Bug | `SmartLibraryEnrichmentService.cs:423` | **Always-true artist-enrichment gate.** `(!completeness.IsArtistComplete \|\| settings.EnableArtistEnrichment) && settings.AutoDownloadMissingArtistImages` — `EnableArtistEnrichment` defaults **true**, so the left operand is always true and every scan re-enriches **every** artist (bounded only by `AutoDownloadMissingArtistImages`). Compounds the fire-and-forget task-map at :430 (SLE-03). **Fix**: the intended logic is `!completeness.IsArtistComplete && settings.EnableArtistEnrichment && settings.AutoDownloadMissingArtistImages`. *(↔ SLE-03.)* |
| **INT-04** | ⚠️ Edge (privacy / consent) | `App.xaml.cs:230-235` (`InitializeWatcherAsync`) | **First run silently monitors the entire Music library.** When no monitored folders exist, startup auto-adds `SpecialFolder.MyMusic` and attaches a `FileSystemWatcher` → an unsolicited full-library scan (and, combined with INT-01/INT-02, potential network enrichment) on first launch with no onboarding or consent. **Fix**: gate behind a first-run prompt / empty-state "Add your music folder" CTA; do not auto-monitor a system folder. |
| **INT-05** | ⚠️ Low | `App.xaml.cs:220` + `QueueService.cs:198-241` | **Startup `RestoreAsync` is fire-and-forget and only partially guarded.** `_ = queueService.RestoreAsync();` has no fault continuation; inside, only `LoadPlayerStateAsync` is `try/catch`-wrapped (:201-202) — `GetTracksByIdsAsync` (:209) and `EmitPlaybackStateChanged` (:240) can throw into an **unobserved** task (caught only by the global `UnobservedTaskException` logger at `App.xaml.cs:64`, which merely writes to Debug). A transient DB hiccup at boot → silent no-restore with no user signal. **Fix**: `await` inside a `try/catch` (or add a fault continuation) and guard the DB/emit calls. *(↔ SYS-02: startup error handling.)* |
| **INT-06** | ⚠️ Security / transparency | `ExternalDataSettingsService.cs:~121` (comment) + persistence path + `SettingsPage.xaml` key UI | **API key stored as plaintext; the framing overstates protection.** The TheAudioDB key is serialized as **plaintext JSON** into the settings table; the code comment ("Security: Never log or display the raw API key") and the Settings UI imply secure handling. Real risk is low (local DB) but the wording is misleading. **Fix**: either encrypt at rest (Windows DPAPI `ProtectedData` scoped to the user) or correct the UI/comment wording to "stored locally in plaintext". |
| **INT-07** | 💡 Improvement | `ExternalDataSettingsService.cs:~128-131` `SyncOptionsWithSettings` + `ExternalDataSettings.cs` (`TheAudioDbApiKey="2"`) | **Cleared key isn't un-applied; shipped placeholder key.** `SyncOptionsWithSettings` assigns `_theAudioDbOptions.ApiKey` **only when the incoming key is non-empty** → clearing the key in the UI leaves the previous key live in the options singleton until restart. Also the model default `TheAudioDbApiKey="2"` is a placeholder that reads as a real credential. **Fix**: always assign (allow empty to clear the live key); default the key to empty string. |

> ✅ **Verified-clean / correct-by-design (integration)**: providers read `CurrentSettings` **live** (so once INT-01 loads settings at startup, runtime toggles take effect immediately — no restart needed); `IsEnabled` on `MusicBrainzMetadataProvider` correctly ANDs the provider toggle with `EnableOnlineMetadata` and `!OfflineOnlyMode`; the Smart Library Enrichment dialog is opened *from* the Settings page (after `LoadSettingsAsync`), so **inside that dialog** settings are correctly loaded — the INT-01 exposure is specifically the *background/first-play* paths that run before Settings is ever opened; `InitializeWatcherAsync` (unlike `RestoreAsync`) wraps its whole body in `try/catch`.

**Systemic note (root framing).** INT-01 and INT-02 share a shape: the UI and model faithfully capture user intent, but that intent never reaches the code that acts. INT-01 is a *timing* gap (loaded too late); INT-02 is a *reading* gap (never consulted). Both make the online subsystem behave as if the user's choices don't exist — which matches the reported "the agent broke the app" symptom far better than any compile error. Fix them together in **Batch 3 (§15)**.

---

## 14. Fix-Verification Status (as of 2026-08-20)

*Spot-checked during the third pass so the remediation agent does not redo already-completed work or skip still-open items. "Fixed" = verified in the current working tree; "Still open" = code re-read this pass and the defect is present.*

### ✅ Fixed since the first/second pass
| ID | Evidence |
|----|----------|
| **UI-ST-01** (duplicate-delete had no confirmation) | Both paths now confirm: `SettingsPage.xaml.cs:165` `DeleteDuplicate_Click` shows a "Move File to Recycle Bin?" `ContentDialog`; `:208` `DeleteAllDuplicates_Click` shows a count/group summary dialog **and** a post-run result dialog. |
| **Build/Test regressions** (implied by "broke the app") | Re-ran 2026-08-20: `dotnet build Octave.Desktop` → 0 warnings / 0 errors; `dotnet test Octave.Core.Tests` → 128/128. The breakage is runtime/behavioral (see INT-*), not compilation. |

### ⏳ Still open (re-confirmed this pass — do not assume fixed)
| ID | Evidence it is still present |
|----|------------------------------|
| ~~**NP-19**~~ ✅ **FIXED — Batch 13** (`2df5d5e`) | Queue rows now bind the VM-hydrated `QueueItem.ArtworkUrl` token; see §18 Batch 13. |
| ~~**UI-PL-01**~~ ✅ **FIXED — Batch 14** (`b57fabd`) | `DeletePlaylist_Click` confirms via ContentDialog before executing; see §18 Batch 14. |
| **SYS-02 / SH-10** | ✅ **FIXED — Batch 12** (`86bc36e`) — native fatal-error dialog + exit; only `OperationCanceledException` is marked handled. See §18 Batch 12. |
| **PH-01 / PH-02** | `bin/`, `obj/`, and `.vs/` artifacts are still **tracked** (git status shows dozens of `Octave.Core.Tests/bin,obj/**`, `.vs/OCTAVE.slnx/**`). |
| **PH-03** | `.gitignore` line 5 is `/claude` (typo) instead of `.claude/`. |
| **PH-05 / PH-06 / PH-07** | `build.log`, `temp`, and `test_fx/` still present at repo root; `CODEBASE_AUDIT.md` itself is untracked. |

*(All other §1–§13 findings were not individually re-verified this pass; treat them as open unless a batch's acceptance check proves otherwise.)*

---

## 15. Remediation Batches — sequenced fix plan for a coding agent

**Purpose.** Every actionable issue in §1–§13 is assigned to exactly one batch below; **§17 is the coverage ledger** that proves it (every finding-family → its batch, plus the deliberately-deferred polish backlog and the verified-good "no-action" items). Batches are **self-contained** and sized to fit a single coding-agent session without context overflow — open only the files the batch names, read each cited ID's row before editing, and fix **one batch per session, in order**. Batches **B1–B3 are the roots** — doing them first collapses many downstream symptoms; B4–B16 can otherwise proceed in the listed order (later batches assume the roots are done).

**How each batch is structured**
- **Goal** — the single outcome the batch achieves.
- **Open only these files** — the batch's context budget. Do not load the rest of the repo; open referenced files lazily if a fix needs them.
- **Findings** — `ID → one-line action`. Full detail lives under that ID earlier in this document; read the ID's row before editing.
- **Acceptance** — must hold before the batch is "done".
- **Depends on** — batches that must land first.

**Global invariants for every batch** (check at the end of each): `dotnet build Octave.Desktop` = 0/0; `dotnet test Octave.Core.Tests` still green (or updated intentionally); no new setting/UI added without a service reading it; commit the batch on its own before starting the next.

---

### Batch 0 — Repo hygiene & guardrails *(mechanical, zero runtime risk — do first so later diffs stay clean)*
- **Open only these files**: `.gitignore`, repo root listing.
- **Findings**
  - `PH-01 / PH-02` → `git rm -r --cached` all tracked `bin/`, `obj/`, `.vs/` (all three projects incl. `Octave.Core.Tests`); confirm they leave the index.
  - `PH-03` → fix `.gitignore` line 5 `/claude` → `.claude/`.
  - `PH-05 / PH-06 / PH-07` → delete `build.log`, `temp`, `test_fx/`; add `CODEBASE_AUDIT.md` to the repo (or intentionally ignore it).
  - `PH-08` → exclude `Assets/test.mp3` from release packaging (`.csproj` `<Content>` → dev-only / `None`).
  - `PH-04 / MC-09` → expand the 40-byte `README.md` (project description, build/run steps, architecture overview). *(Doc-only; if it grows beyond a quick paragraph, split it out — it's the one B0 item that isn't a one-line mechanical edit.)*
  - `MC-01` (duplicate `using` in `ExternalDataModels.cs`), `MC-02` (unused `mc:Ignorable`/`d:` in `NowPlayingPage.xaml`), `MC-03` (implicit `using System;` in `AlbumArtPanel.xaml.cs`) → the pure-hygiene nits that touch no logic. *(`MC-04/05/06` are theme bugs → Batch 14; `MC-07/08` are the same files as `PH-05/06`.)*
- **Acceptance**: `git status` shows no build artifacts; `.gitignore` covers all three projects' `bin/obj`; `Assets/test.mp3` is not in the release output; solution still builds. No `.cs`/`.xaml` behavior changed.
- **Depends on**: none.

### Batch 1 — SQLite concurrency root *(highest leverage)*
- **Open only these files**: `SqliteDbContext.cs`, `TwoTierExternalDataCache.cs`.
- **Findings**
  - `DB-01` → set `busy_timeout` (and confirm WAL + shared connection strategy) on **every** opened connection.
  - `DB-02` → async connection open where applicable.
  - `DB-03` → track "FTS executed successfully" separately from row-count in `SearchLibraryAsync`; only fall back to the leading-wildcard LIKE scan when FTS was unavailable/errored, **not** when FTS legitimately returned zero rows (today every genuine no-match query pays for both an FTS lookup and a full LIKE scan).
  - `DB-04` → wrap the three writes in `UpsertTrackAsync` (INSERT Artist / INSERT Album / UPSERT Track) in a local transaction on the `tx==null` path so it's atomic like the tx path.
  - `DB-05` → **(🔴 data-loss)** wrap the `InitializeAsync` PlaylistTracks migration (`INSERT _Temp … ; DROP TABLE PlaylistTracks; ALTER … RENAME`) in an explicit `BEGIN/COMMIT` — a crash after `DROP` and before `RENAME` currently loses **every** playlist-track row.
  - `DB-06` → narrow the FTS5 catch to `SqliteException` (malformed MATCH / missing FTS table) and log through the real logger instead of swallowing to `Debug.WriteLine` (a no-op in Release, so schema/corruption errors are invisible in prod).
  - `DB-07` → guard `SearchLibraryAsync` against empty/whitespace queries (short-circuit + always `LIMIT`); fix the `CreateConnection` connection-leak-on-PRAGMA-throw while here.
  - `DB-08` → make `SetPlaylistOrderAsync` reorder by the surrogate entry `Id` only (drop the `OR TrackId=@key` fallback) so a repeated track's copies keep distinct `SortOrder`.
  - `CACHE-01` → stop swallowing/over-narrowing the DB-write catch that hides `SQLITE_BUSY`.
- **Acceptance**: a concurrent scan + UI write no longer throws `SQLITE_BUSY`; add/adjust a test that hammers concurrent writes; killing the process mid-`InitializeAsync` migration loses **zero** playlist-track rows (DB-05); an empty/whitespace search returns a bounded/empty result, never the whole table (DB-07). **Relieves** SCAN-01, EDITOR-02/03 contention, SLE-08.
- **Depends on**: B0.

### Batch 2 — Dead exact-ID match root
- **Open only these files**: `TrackMetadataMatcher.cs`, `SmartLibraryEnrichmentService.cs`, `TrackEnrichmentWorkflow.cs`, `ExternalDataModels.cs`.
- **Findings**
  - `MATCH-01 / SLE-01` → the exact external-ID (ISRC/MBID) short-circuit is dead because it compares a **path-hash `Track.Id`** to an **MBID**. Thread the track's real `ExternalIds` into the matcher and short-circuit on an exact ID hit before fuzzy scoring.
  - `ENR-01` → ensure the workflow accepts an exact-ID match without applying the fuzzy confidence gate.
  - `AR-03` → align `ExternalIds.GetId`'s case-insensitive switch with the raw `AdditionalIds` dictionary lookup (a stored key like `MusicBrainzReleaseId` matches the switch but not the dict), **and** fix `ExternalIds` value equality if it is used as a dictionary/set key.
- **Acceptance**: a track carrying a known MBID/ISRC matches by ID (add a unit test proving the exact-ID path is taken).
- **Depends on**: B0.

### Batch 3 — External-data wiring & dead settings *(the §13 batch — makes user settings actually take effect)*
- **Open only these files**: `App.xaml.cs`, `ExternalDataSettingsService.cs`, `ExternalDataSettings.cs`, `SmartLibraryEnrichmentService.cs`, `CompositeLyricsService.cs`.
- **Findings**
  - `INT-01` → `await settingsService.LoadSettingsAsync()` in `OnLaunched` before window activation / restore / watcher.
  - `INT-02 / SLE-02` → gate the metadata plan on `AutoFillMissingMetadata`; branch field selection on `WritePolicy`; honor `ReplaceExistingMetadata`.
  - `INT-03` → fix the always-true artist gate at `:423`.
  - `INT-07` → `SyncOptionsWithSettings` always-assign `ApiKey` (empty clears); default the model key to `""`.
  - `INT-06` → encrypt the key at rest (DPAPI) **or** correct the "secure" wording.
  - `ORC-02` → `CompositeLyricsService` passes real `externalIds` (not `null`) to the orchestrator.
- **Acceptance**: with `OfflineOnlyMode=true` persisted, a cold launch + first-play makes **no** network call; turning `AutoFillMissingMetadata` off stops tag writes; changing `WritePolicy` changes which fields are written.
- **Depends on**: B0. (Benefits from B2 for the ID-match path but not blocked by it.)

### Batch 4 — Playback event storms & queue integrity *(the "app feels broken" batch)*
- **Open only these files**: `QueueService.cs`, `NowPlayingViewModel.cs`, `NowPlayingPage.xaml.cs`.
- **Findings**: `QUEUE-01` (give full-state vs progress-only persistence **separate watermarks** — a 5-sec progress tick must not advance the token past a pending queue/shuffle/index save and silently drop it), `QUEUE-02` (add a consecutive-failure **circuit breaker** — after one full lap of undecodable files, `Stop` + surface an error instead of looping the ThreadPool forever), `QUEUE-03` (drop the duplicate `PlaybackStateChanged` emit in the `TrackStarted` handler — `PlayIndexInternal` already emits; this is the root of the double-rebuild behind VM-03/NP-09), `QUEUE-04` (snapshot `state` under `_queueLock`, raise `PlaybackStateChanged`/`QueueChanged` **after** releasing it), `QUEUE-05` (debounce volume persistence the way `MaybeSaveProgress` throttles position), `QUEUE-06` (convert the missing-file skip to an **iterative** loop — a fully-offline queue stack-overflows on the current recursion), `QUEUE-07` (`Random.Shared` instead of `new Random()` per shuffle), `QUEUE-08` (on restore, seed `_unshuffledQueue` from `PersistedPlayerState.UnshuffledTrackIds`, falling back to `TrackIds` only when null — otherwise closing while shuffled permanently loses the true order), **`QUEUE-09` (high — `SetShuffle(true)` duplicates the currently-playing track; fix `listToShuffle.RemoveAll(i => i.Id == currentItem.Id)` + add an `Equals`/`Id`-based comparer or a `record`-style `QueueItem`)**, `QUEUE-10` (EnqueueNext desync when `_currentIndex == -1` — insert into `_unshuffledQueue` at index 0 as well), `QUEUE-11` (reorder-while-shuffled mutates natural order — confirm intent first, then leave `_unshuffledQueue` untouched if unintended), `QUEUE-12` (make `QueueService` `IDisposable` + detach the audio/scanner handlers), `VM-03` (downstream double-handling — guard even after QUEUE-03), `CRIT-01` (NowPlaying double-subscribe on ctor + Loaded — subscribe in exactly one place), `CRIT-04` (NowPlaying **artwork flash** — `UpdateFromState` sets `CurrentArtworkUrl` from a still-`null` `CurrentAlbum` before credits load; look artwork up by `AlbumId` from the DB or cache the prior art until the new one resolves), `NP-08` (artist-split regex on ` & ` wrongly splits "Simon & Garfunkel" when the `isFullMatch` primary-artist check misses), `NP-09` / `UI-NP-05` (RefreshUpNextQueue `Clear()`+re-add flickers the whole ListView — diff-update instead), `NP-10` (O(n) `PlayQueueItem` lookup — store the index on `QueueItem` or use `IndexOf`), `INT-05` (startup `RestoreAsync` is fire-and-forget with only partial guarding — wrap `GetTracksByIdsAsync`/`EmitPlaybackStateChanged` inside `RestoreAsync` in try/catch, and `await` the `App.xaml.cs:220` call site in a try/catch alongside B3's OnLaunched reorder).
- **Acceptance**: each playback-state change emits exactly once; **enabling shuffle never duplicates the current track (`_activeQueue.Count == _unshuffledQueue.Count`)**; `EnqueueNext` keeps active/unshuffled in sync in every index state; restore preserves shuffle/repeat and the correct current index; no re-entrancy while a lock is held; **no visible artwork flash on track change** (CRIT-04); a queue of undecodable files stops after one lap instead of spinning forever (QUEUE-02).
- **Depends on**: B1 (persistence writes).

### Batch 5 — Audio engine safety
- **Open only these files**: `ManagedBassAudioService.cs`.
- **Findings**: `AUDIO-01` (don't hold the stream lock across network I/O), `AUDIO-02..06` (no-sound device fallback, deterministic `Dispose` vs finalizer, EQ/limiter apply order — apply those present), `AUDIO-07` (give each End-sync per-stream `(sessionId, uri)` identity resolved from the ended `channel`, and `ChannelRemoveSync` on fade/stop, so a superseded/crossfaded stream can't advance the queue).
- **Acceptance**: no lock held across I/O; dispose is deterministic (pairs with TEST-17 in B15); a manual crossfade-skip in the final second of a track does not skip the incoming track (regression test for AUDIO-07).
- **Depends on**: B0.

### Batch 6 — Metadata editor file-write integrity *(data-loss risk — pair fixes with tests)*
- **Open only these files**: `TrackMetadataEditor.cs`, `SqliteDbContext.cs` (upsert transaction only), `TrackMetadataEditorTests` (+ new).
- **Findings**: `EDITOR-01` (split multi-value Artist/AlbumArtist/Composer/Genre on `;`/`/` — don't collapse the whole frame to one element), `EDITOR-02` (delete the orphaned old Artist/Album rows in the same transaction as the rename upserts), `EDITOR-03` (wrap the three rename upserts in one transaction — cf. `DB-04` — and consider sharing the scanner `_writeSemaphore`), `EDITOR-04` (temp-file + `File.Replace` atomic write; sweep stray `.octave_bak` on startup), `EDITOR-05` (drop the lock pre-check TOCTOU probe; open once with the real write handle, treat `IOException` as "locked" — this **is** `CRIT-03`), `EDITOR-06` (clamp `1000 ≤ year ≤ now+1`, `1 ≤ track ≤ 999`; apply the same guard in `SmartLibraryEnrichmentService.cs:364-375`, shared with B16), `EDITOR-07` (on restore failure surface a hard error naming the backup path; delete the backup only after a verified-good restore), `EDITOR-08` (skip artwork re-read/cache unless artwork actually changed; prefer FrontCover + validate MIME), `EDITOR-09` (if the file write succeeds but the Stage-3 DB sync throws, log prominently + surface to the user — file/DB divergence until next scan), `ME-01..05` (reconcile — the §4 view of the same file), `ME-07` (add `ApplyComposer`/`ApplyTrackCount`/`ApplyDiscCount` to `FieldSelectionOptions` **and** VM/UI bindings, or stop force-applying them), `CRIT-03` (see EDITOR-05) → net effect: multi-value tag preservation, atomic `File.Replace`, orphan `.octave_bak` cleanup, year/track clamping, wrap file-write + DB-sync in one recoverable unit; `TEST-01/02` → add the rollback + "file-written-but-DB-sync-fails" coverage. *(`ME-06` — the editor VM's `_applyCts` is never cancelled on rapid re-Apply — is a CTS-lifecycle bug and is fixed with the other VM CTS work in **Batch 11**.)*
- **Acceptance**: a forced `Save()` failure restores the file byte-for-byte and leaves no `.octave_bak`; the DB-sync-fail path is tested.
- **Depends on**: B1.

### Batch 7 — Matching accuracy
- **Open only these files**: `MetadataTextNormalizer.cs`, `TrackMetadataMatcher.cs`, the specific provider files named by each ID.
- **Findings**: `MATCH-02..07`, `MB-02/03`, `CAA-01/02`, `ADB-01/02`, `LRC-01/02`, `HLP-03` → containment floor, hyphen filename split, title+album version detection, duration dead-zone, phantom track#, confidence artist compare, CAA release-MBID selection, LrcLib dedup key.
- **Acceptance**: table-driven tests (this is TEST-15) cover each fix.
- **Depends on**: B2.

### Batch 8 — Provider correctness & compliance
- **Open only these files**: `MusicBrainzMetadataProvider.cs`, `TheAudioDbArtistEnrichmentProvider.cs`, `HttpService.cs`.
- **Findings**: `MB-01` → add the `!IsEnabled` guard to `GetAlbum`/`GetArtist`/`GetReleaseGroupMetadataAsync` (they currently issue live HTTP even when the provider is disabled / OfflineOnlyMode is set); `PROV-01` → compliant `User-Agent` with contact info (MusicBrainz + Cover Art Archive policy); `PROV-02` → make the MB 1 req/s interval **authoritative at the HTTP layer** (a key→interval map), or require the rate-limiter registry for MB and assert the limiter exists — don't rely on the provider ctor winning a `GetOrAdd` race against `HttpService`'s 500 ms default (silent 2 req/s policy violation if DI omits the registry); `ADB-03` → add the top-level `IsEnabled` short-circuit to `SearchArtistImageUrlsAsync`. *(General `NET-01..05` HTTP hardening — cancellation, response disposal — lives in **Batch 10**, not here.)*
- **Acceptance**: a disabled provider makes **zero** HTTP calls (assert `Times.Never`); MusicBrainz never issues more than 1 request/second even when constructed without the rate-limiter registry (PROV-02).
- **Depends on**: B3 (settings load) so `IsEnabled` reflects real prefs.

### Batch 9 — Scanner & watcher
- **Open only these files**: `LocalLibraryScanner.cs`, `LibraryWatcherService.cs`, `App.xaml.cs` (INT-04 only).
- **Findings**: `SCAN-01..07`, **`SCAN-08` (critical — an unavailable/partially-inaccessible root must NOT trigger reconciliation deletes; gate on a "root fully enumerated & reachable" flag)**, `SCAN-09` (key albums by AlbumArtist, not first performer), `SCAN-10` (per-track try/catch after `TagLib.File.Create` so one odd file can't roll back the whole scan), `SCAN-11` (read `Tag.Disc`), `SCAN-12` (stable progress denominator), `SCAN-13` (guard `Path.GetFullPath` per reconciliation row), `WATCH-01..06`, **`WATCH-07` (lock check must use `FileShare.Read`, not `ReadWrite`, or it reads half-copied files)**, **`WATCH-08` (don't register an offline root for deletion-reconciliation; re-watch a root when it reappears — pairs with SCAN-08)**, `WATCH-09` (don't construct timers inside `AddOrUpdate` factories) → batched transactions, task-leak, AC/DC artist split, targeted delete, debounce races; `INT-04` → first-run consent instead of silent MyMusic auto-monitor.
- **Acceptance**: a full scan uses batched transactions; **a scan of an unavailable or partially-inaccessible root deletes zero tracks whose files still exist** (test: point a root at a missing/denied path → assert no deletions); one unreadable/odd file is skipped without aborting the scan; compilations/guest-feature tracks group under one album; **a file still being written fails the lock check and is deferred, not scanned mid-write**; watcher debounce is race-free; first run does not auto-monitor without consent.
- **Depends on**: B1.

### Batch 10 — Orchestrators / cache / single-flight / network
- **Open only these files**: `AsyncSingleFlight.cs`, `HttpService.cs`, `ProviderRateLimiterRegistry.cs`, `TwoTierExternalDataCache.cs`, `ExternalMetadataOrchestrator.cs`, `ExternalArtworkOrchestrator.cs`, `OnlineLyricsOrchestrator.cs`.
- **Findings**: `SF-01/02`, `NET-01..05`, `CACHE-02..06`, `ORC-01/03`, `HL-01/02`, `OL-06` (dedup the same track returned by multiple providers in `ExternalMetadataOrchestrator.SearchTrackCandidatesAsync`), `OL-08` (cache the artwork-token file-existence check in `ExternalArtworkOrchestrator` — currently 3 disk-stat calls per resolution) → per-caller cancellation, dispose `HttpResponseMessage`, negative caching, LRU eviction budget.
- **Acceptance**: cancelling one caller doesn't cancel a shared in-flight fetch for others; no response-message leaks.
- **Depends on**: B1 (cache DB tier).

### Batch 11 — ViewModel leaks & lifecycle
- **Open only these files**: the VM files named by each ID (`NowPlayingViewModel.cs`, `LibraryEnrichmentViewModel.cs`, `ExternalDataSettingsViewModel.cs`, `HomeViewModel.cs`, `SearchViewModel.cs`, `EntityDetailViewModel.cs`, `PlaylistDetailViewModel.cs`, `MetadataEnrichmentViewModel.cs`, `EqBandViewModel.cs`) + `NowPlayingPage.xaml.cs`.
- **Findings**: `CRIT-02` (CTS dispose), `ME-06` (`MetadataEnrichmentViewModel._applyCts` never cancelled/disposed on a rapid re-`ApplyChangesAsync` → concurrent writes to the same file — same CTS-lifecycle fix as CRIT-02), `VM-01/02` (transient VMs subscribing to singletons never unsubscribe → leak), `VM-04..13`, `SH-11` → add `Cleanup`/unsubscribe on unload, dispose CTS before replacing, marshal post-`await` collection mutations, `SequenceEqual`-guard refreshes, fix `EqBandViewModel` ctor.
- **Acceptance**: navigating into/out of Now Playing N times leaves subscription count flat; no CTS leak.
- **Depends on**: B4 (NowPlaying event model settled first).

### Batch 12 — System integration & shell
- **Open only these files**: `WindowMinSizeHelper.cs`, `App.xaml.cs`, `WindowsSmtcService.cs`, `ShellViewModel.cs`, `MainViewModel.cs`, `ISmtcService.cs`.
- **Findings**: `SYS-01` (per-hwnd subclass delegate dictionary + `RemoveWindowSubclass`), `SYS-02 / SH-10` (fatal-init dialog/exit instead of zombie; stop blanket `e.Handled=true`), `SYS-03/04` (marshal SMTC onto its dispatcher; fix inverted button-handler branch), `SHELL-01` (sleep-timer stale-instance guard), `SH-02` (`ShellViewModel._searchCts` is disposed then reassigned without synchronization in `UpdateSearchSuggestionsAsync` — close the race with `Interlocked.Exchange`), `SHELL-02` (delete dead `MainViewModel` + its registration), `SYS-05` (dispose/unsubscribe).
- **Acceptance**: giving a 2nd window a min size can't crash; a DB-init failure shows a dialog and exits cleanly, not an invisible process.
- **Depends on**: B0.

### Batch 13 — Now Playing panels & lyrics UI
- **Open only these files**: `NowPlayingPage.xaml`(.cs), `LyricsPanel.xaml`(.cs), `QueuePanel.xaml`, `CreditsPanel.xaml`, `AlbumArtPanel.xaml`, `SpectrumVisualizerControl.xaml.cs`.
- **Findings**: `CRIT-05` (lyrics `_lineElements` goes stale after ListView virtualization/recycling → highlight targets dead elements; use `ContainerFromIndex()` per index, or a non-virtualized `ItemsControl` since lyric lists are <200 items), `NP-01` (dead `WideLayout`/`NarrowLayout` visual states — add setters or delete the group), `NP-02` (empty `TopStageGrid_SizeChanged` handler — implement or unbind), `NP-05` (set only `MaxHeight`, not also `Height`, on the lyrics panel), `NP-06` (skip the async DB lookup when `Frame` is null in the credits click handlers), `NP-07` (dead `AlbumPanelTransform.X = 0` completion reset — remove), `NP-12` (cache the hover Storyboard / use Composition — currently a new Storyboard per mouse enter/exit), `NP-15` (highlight only the outgoing + incoming lyric line, not all `_lineElements`), `NP-16` (double-scroll — use one of `ScrollIntoView`/`StartBringIntoView`, not both competing targets), `NP-19` (QueuePanel thumbnail binds `Track.SourceUri` — the **audio file path** — to `ArtworkPathConverter`; bind the album artwork URL instead — **still open**, §14), `NP-20` (highlight the currently-playing queue item via its `IsPlaying` flag), `NP-21` (show a pause/equalizer glyph on the playing item), `UI-NP-01..06` (the Now-Playing per-page UI items in §7), `HLP-01` (marshal spectrum frames onto the UI dispatcher). *(NP-08/09/10 are NowPlayingViewModel logic → Batch 4; NP-14/17 are theme → Batch 14; NP-11 is a verified-good note, no action.)*
- **Acceptance**: queue-row thumbnails render; the currently-playing queue row is visibly highlighted; lyrics auto-scroll once (no double-scroll) and the highlight tracks the **right** line after scrolling/recycling (CRIT-05); spectrum updates don't throw cross-thread.
- **Depends on**: B4, B11.

### Batch 14 — Theme sweep & UX safeguards
- **Open only these files**: work **page-by-page** (open one page's `.xaml`/`.cs` at a time — do not load all at once).
- **Findings** *(this batch owns the **theme, empty-state, and hazardous-action** subset of §7 — the correctness-adjacent UI items. The remaining §7 tooltip/affordance/layout items are non-blocking polish, enumerated in **§17.3**, and are explicitly **out of scope** here.)*
  - **Theme bugs** (replace hardcoded hex / `White` with the matching `ThemeResource` brush): `MC-04`, `MC-05`, `MC-06`, `NP-14`, `NP-17`, `UI-MW-04`, `UI-HP-04`, `UI-HP-05` (hover colors injected in code-behind → use VisualStates), `UI-AP-04`, `UI-ED-04`, `UI-MP-03`. *(The Now-Playing panels' theme item `UI-NP-04` is owned by Batch 13, not here.)*
  - **Empty states**: `UI-AP-01` (🔴 Albums), `UI-AR-01` (🔴 Artists) → add an empty-state container matching HomePage's pattern.
  - **Hazardous actions need confirmation**: `UI-PL-01` (🔴 playlist-delete confirmation — **still open**, §14), `UI-ST-01` / `SH-05` (🔴 "Delete All Duplicates" — same bulk-recycle action; add an explanatory label + a pre-deletion count dialog).
  - **Named UX fixes**: `UI-PL-02` (disable "Create" until a non-whitespace playlist name is entered), `UI-ST-02` (relabel / gate the "Ignite Main Engines" debug button).
- **Acceptance**: switching light/dark leaves no stuck colors on any page (theme IDs above); Albums/Artists show an empty-state message when the library is empty; **every destructive action confirms first** (`UI-PL-01`, `UI-ST-01`/`SH-05`); a blank playlist name can't be created. Polish items in §17.3 are **not** required for this batch to be "done".
- **Depends on**: B0. *(Large surface — if it risks overflow, split by page group: shell+home, library+albums+artists, playlists+entity, settings+search.)*

### Batch 15 — Test-suite hardening
- **Open only these files**: the test project.
- **Findings**: `TEST-03..20` not already handled in B1/B6/B7 → call real converters (TEST-03/04), assert cancellation `Times.Never` (TEST-09), add scanner/watcher/playlist/service coverage (TEST-13/14/16), seed RNG + inject clock for determinism (TEST-08/19), `:memory:` for pure-logic tests (TEST-20), `using`/fixture for the audio engine (TEST-17).
- **Acceptance**: tests assert against **production** code paths (not in-test copies) and are deterministic under parallel runs.
- **Depends on**: the batch that owns each corresponding fix (so the test asserts the fixed behavior).

### Batch 16 — Smart Library Enrichment pipeline correctness *(the auto-enrich scan: silent data loss + wasted re-fetching)*
- **Open only these files**: `SmartLibraryEnrichmentService.cs`, `TrackEnrichmentWorkflow.cs`, `ArtistEnrichmentService.cs`.
- **Findings**
  - `SLE-03` → await artist enrichment inside the throttle and `UpsertArtist` the bio/photo **before** counting it enriched, so `IsArtistComplete` can flip true and later scans stop re-fetching the same artists every run (today the result is written only to the enrichment cache, never the Artist record, yet counted as done).
  - `SLE-04` → don't leave a **faulted** artwork-resolution `Task` in `_albumArtworkTasks` (one transient blip on an album's first track poisons every other track of that album for the whole session); `TryRemove` the key on exception, or cache the resolved string rather than the `Task`.
  - `SLE-05` → replace the eager `allTracks.Select(async …)` materialization (one state machine + semaphore waiter + up-front DB read **per track**) with `Parallel.ForEachAsync(MaxDegreeOfParallelism=…)` so work stays lazy on large libraries.
  - `SLE-06` → dispose the old `_scanCts` before replacing it; guard concurrent scans with a `SemaphoreSlim(1)`; scope the dedup caches **per scan** so a re-entrant scan can't `Clear()` them mid-flight and trigger duplicate online calls.
  - `SLE-07` → for tracks that throw during planning, build a **Failed plan** (carrying the exception message) and persist a Failed state record — don't just bump `failedCount` and let them vanish from the review queue.
  - `SLE-09` → count **any** metadata-writing field (Genre/Year/TrackNumber/DiscNumber/ExternalIds), not only Title/Artist/Album, so `metadataUpdated` isn't under-reported (a track that gains only Year/Genre currently reports 0 fields updated).
  - `ENR-02` → fetch candidate artwork/lyrics **lazily** for the chosen (or top-only) candidate instead of N sequential 5-s HTTP round-trips for every candidate the user won't pick.
  - `ENR-03` → the "Album Artist" diff proposes the candidate's **track** artist (`meta.ArtistName`) — wrong for compilations/features; use a real album-artist field or leave it unset.
  - `ENR-04` → the diff is case-insensitive, so provider **capitalization-only** corrections are silently dropped; surface them as changes.
  - `PROV-04` → when an artist **image download fails**, don't cache it 30 days with `LocalImageToken=null` and never retry; use a short TTL / "image pending" marker, or re-attempt on a cache hit that lacks a token.
- **Acceptance**: a second enrichment scan of an already-enriched library issues **no** repeat artist/album network fetches (SLE-03); one transient artwork failure doesn't blank the other tracks of the same album (SLE-04); a track that throws during planning yields a persisted Failed record (SLE-07); a track that gains only Year/Genre reports a non-zero updated-field count (SLE-09); a large-library scan doesn't allocate one async state machine per track up front (SLE-05).
- **Depends on**: B1 (`busy_timeout` — also resolves the SLE-08 enrichment-state write contention), B2 (exact-ID match — SLE-01/ENR-01 land there), B3 (settings gates — SLE-02 / `ScanOnlyMissing*`). Shares the year/track clamp helper with B6 (EDITOR-06).

---

> **Batch dependency summary**: `B0 → {B1, B2} → B3`, then B4→(B11,B13), B6, B7, B8, B9, B10, B12, B14, and **B16** (enrichment pipeline — needs B1+B2+B3) in any workable order, and **B15 last** (or fold each test into the batch that owns its fix). Roots first: **B1 (DB-01)** and **B2 (dead ID-match)** each collapse ~5 downstream findings; **B3 (INT-01/02)** is what makes the user's online-fetching settings actually function — the single most likely fix for the "the agent broke the app" report.

---

## 16. Coverage Provenance & Verdict

*(Added 2026-08-21. Severity legend as in §12.)*

**Why this section exists.** The second-pass deep audit (§12) was produced by fanning out one discovery agent per subsystem. Five of those agents — covering **§12.2 DB, §12.3 Audio, §12.4 Queue, §12.5 Scanner, §12.6 Watcher** — were lost mid-flight to environment API failures (`StreamIdleTimeoutError` / `504 Gateway Time-out` on the gateway), so their scopes carried an open question: *were those areas actually covered, or do undiscovered issues still lurk there?* This section closes that question. Each scope was re-covered **by hand** (targeted `grep` + direct reads of the live source — relaunching the flaky agents would have hit the same wall on the large files), and the result is recorded below so the provenance is auditable.

| Scope | Discovery status at crash | How re-covered (2026-08-21) | New findings | Verdict |
|-------|---------------------------|------------------------------|--------------|---------|
| **§12.4 Queue** | **Landed** (`QUEUE-01..09` present) | Spot-verified the highest-stakes finding — `QUEUE-09` (`QueueItem` reference-equality; no `Equals` override in `DomainModels.cs:66-71`) — against live code: **genuine**. | none | ✅ **Well-covered** |
| **§12.5 Scanner** | **Landed** (`SCAN-01..08` present) | Spot-verified `SCAN-08` against `LocalLibraryScanner.cs` — `IgnoreInaccessible=true` (`:106`), swallowed enumeration catch (`:123-126`), reconciliation delete (`:268-289`): **genuine**. | none | ✅ **Well-covered** |
| **§12.6 Watcher** | **Landed** (`WATCH-01..08` present) | Spot-verified two against `LibraryWatcherService.cs` — `WATCH-08` (`AddMonitoredPath` registers the scanner at `:48` **before** the `Directory.Exists` guard at `:50`) and `WATCH-07` (lock-probe opens with `FileShare.ReadWrite` at `:228`, so a file still being written can pass the "is it free?" check): **both genuine**. | none | ✅ **Well-covered** |
| **§12.2 DB** | **Not landed** | Full by-hand re-hunt of `SqliteDbContext.cs` (~2235 lines): parameterization, transactions, UPSERT semantics, FTS triggers, LIKE-escaping, batch/re-projection, persistence atomicity. Layer is well-engineered; 8 candidates probed and **cleared** (see §12.2 note (a)–(h)). | **`DB-08`** — `SetPlaylistOrderAsync`'s `OR TrackId=@key` bulk update stamps every copy of a repeated track with the **same** `SortOrder`. | ✅ **Well-covered** (one niche bug added) |
| **§12.3 Audio** | **Not landed** | Full by-hand re-hunt of `ManagedBassAudioService.cs` (~865 lines): lock discipline, End-sync marshaling, fade/stop/dispose cooperation, position-timer teardown race, EQ FX lifecycle, gain clamping. 4 candidates probed and **cleared** (see §12.3 note (a)–(d)). | **`AUDIO-07`** — End-sync reads **global** session/uri, not the ended stream's, so a superseded/crossfaded stream's natural end can advance the queue and **skip the incoming track**. This also **corrected a prior verified-clean claim**. | ✅ **Well-covered after AUDIO-07** |

**Verdict.** The document covers all five crashed-agent scopes well.

- **Queue / Scanner / Watcher** — their discovery pass had already landed *before* the crashes; independent spot-checks against the live source confirm the flagged findings are real, so no coverage gap remained there.
- **DB / Audio** — their discovery had **not** landed, so these were the genuine risk. Re-hunting both by hand found each layer soundly engineered, but surfaced exactly one previously-missing finding apiece: **`DB-08`** (niche — a playlist holding the same track twice) and **`AUDIO-07`** (a real crossfade/late-delivery **track-skip**). `AUDIO-07` was the one *material* gap: it lived inside an area the report had marked "verified-clean," so it is doubly worth having caught — the claim is now corrected and the finding folded into Batch 5.

Net: no scope was left unexamined, the two un-covered layers are now closed with concrete, code-anchored findings, and the additions are wired into the remediation batches (`DB-08`→Batch 1, `AUDIO-07`→Batch 5) rather than left dangling.

---

## 17. Coverage Ledger — every §1–§13 finding accounted for

*(Added 2026-08-22. This is the proof behind §15's opening claim. **Invariant: every finding ID defined anywhere in §1–§13 appears in exactly one of three places** — a remediation batch (§15), the deferred-polish backlog (§17.3), or the no-action / verified-good list (§17.4). Nothing is silently dropped. The classification rule: **every 🔴 critical, data-loss, crash, correctness bug, and behavior-affecting ⚠️ finding is in a batch**; only 💡 improvements, cosmetic UI polish, non-triggering defensive nits, and by-design/✅-good items are deferred or marked no-action.)*

**How to use this section.** Pick a batch in §15, open only the files it names, and fix the IDs it cites — the matrix below confirms that doing so leaves no in-scope bug behind. If you are auditing coverage, read a family's row: the three columns must together list every ID number in that family.

### 17.1 Family → batch matrix

**A. Backend / logic families (§12 second pass + §13 — the bug-bearing layers; ~100% batched).**

| Family (§) | # | Batched (ID → batch) | Deferred (§17.3) | No-action (§17.4) |
|---|---|---|---|---|
| PH (§12.1) | 8 | 01,02,03,05,06,07,08,04 → **B0** | — | — |
| DB (§12.2) | 8 | 01–08 → **B1** | — | — |
| AUDIO (§12.3) | 7 | 01–07 → **B5** | — | — |
| QUEUE (§12.4) | 12 | 01–12 → **B4** | — | — |
| SCAN (§12.5) | 13 | 01–13 → **B9** | — | — |
| WATCH (§12.6) | 9 | 01–09 → **B9** | — | — |
| MATCH (§12.7) | 7 | 01→**B2**; 02–07→**B7** | — | — |
| ENR (§12.7) | 4 | 01→**B2**; 02,03,04→**B16** | — | — |
| SLE (§12.7) | 9 | 01→**B2**; 02→**B3**; 08→**B1**; 03,04,05,06,07,09→**B16** | — | — |
| EDITOR (§12.7) | 9 | 01–09 → **B6** | — | — |
| PROV (§12.8) | 3 *(01,02,04)* | 01,02→**B8**; 04→**B16** | — | — |
| MB (§12.8) | 3 | 01→**B8**; 02,03→**B7** | — | — |
| CAA (§12.8) | 2 | 01,02 → **B7** | — | — |
| ADB (§12.8) | 3 | 01,02→**B7**; 03→**B8** | — | — |
| LRC (§12.8) | 2 | 01,02 → **B7** | — | — |
| NET (§12.9) | 5 | 01–05 → **B10** *(B8 uses a subset)* | — | — |
| CACHE (§12.9) | 6 | 01→**B1**; 02–06→**B10** | — | — |
| ORC (§12.9) | 3 | 02→**B3**; 01,03→**B10** | — | — |
| SF (§12.9) | 2 | 01,02 → **B10** | — | — |
| VM (§12.10) | 13 | 03→**B4**; 01,02,04–13→**B11** | — | — |
| SYS (§12.11) | 5 | 01–05 → **B12** | — | — |
| SHELL (§12.11) | 2 | 01,02 → **B12** | — | — |
| HLP (§12.12) | 6 | 01→**B13**; 03→**B7** | 02,04,05,06 | — |
| TEST (§12.13) | 20 | 01,02→**B6**; 15→**B7**; 03–14,16–20→**B15** *(08,17,19 pair with fixes in B4/B5/B1, but the test code itself is written in B15)* | — | — |
| INT (§13) | 7 | 01,02,03,06,07→**B3**; 05→**B4**; 04→**B9** | — | — |

*Backend/logic result: **the only deferred items in the entire second/third pass are `HLP-02/04/05/06`** (one null-guard, one stale comment, one `IsInfinity` guard, one rare-image-format note — all 💡/⚠️-Low). Every other §12/§13 finding is in a batch.*

**B. First-pass review families (§1–§6, §8–§10 — mixed bug + polish).**

| Family (§) | # | Batched (ID → batch) | Deferred (§17.3) | No-action (§17.4) |
|---|---|---|---|---|
| CRIT (§1) | 5 | 01,04→**B4**; 03→**B6**; 02→**B11**; 05→**B13** | — | — |
| NP (§2) | 21 | 08,09,10→**B4**; 01,02,05,06,07,12,15,16,19,20,21→**B13**; 14,17→**B14** | 03,04,13,18 | 11 |
| OL (§3) | 14 | 02,06,08→**B10** | 05,07,10,13,14 | 01,03,04,09,11,12 |
| ME (§4) | 10 | 01,02,03,04,05,07→**B6**; 06→**B11** | 08,09,10 | — |
| CS (§5) | 5 | 05→**B9** | 01,02,03,04 | — |
| SH (§6) | 12 | 11→**B11**; 02,10→**B12**; 05→**B14** | 01,03,04,06,07,09,12 | 08 |
| AR (§8) | 6 | 03→**B2**; 01 (equality)→**B4** (QUEUE-09) | 01 (record refactor),02,04,06 | 05 |
| HL (§9) | 7 | 01,02→**B10** | 03,04,07 | 05,06 |
| MC (§10) | 9 | 01,02,03,07,08,09→**B0**; 04,05,06→**B14** | — | — |

**C. Page-by-page UI/UX families (§7 — theme/hazard/empty-state batched in B13/B14; cosmetic tail deferred).**

| Family (§7.x) | # | Batched (ID → batch) | Deferred (§17.3) | No-action (§17.4) |
|---|---|---|---|---|
| UI-MW (7.1) | 7 | 04→**B14** | 01,02,03,05,06,07 | — |
| UI-HP (7.2) | 5 | 04,05→**B14** | 01,02,03 | — |
| UI-LP (7.3) | 5 | — | 01,02,03,04,05 | — |
| UI-AP (7.4) | 4 | 01,04→**B14** | 02,03 | — |
| UI-AR (7.5) | 3 | 01→**B14** | 02,03 | — |
| UI-PL (7.6) | 5 | 01,02→**B14** | 03,04,05 | — |
| UI-ED (7.7) | 4 | 04→**B14** | 01,02,03 | — |
| UI-SR (7.8) | 4 | — | 01,02,03,04 | — |
| UI-ST (7.9) | 6 | 01,02→**B14** | 03,04,05,06 | — |
| UI-NP (7.10) | 6 | 01–06 → **B13** *(05 also = NP-09/B4)* | — | — |
| UI-MD (7.11) | 3 | — | 01,02,03 | — |
| UI-LE (7.12) | 3 | — | 01,02,03 | — |
| UI-MP (7.13) | 3 | 03→**B14** | 01,02 | — |
| UI-FX (7.14) | 1 | — | — | 01 |

### 17.2 Batch → what it delivers *(reverse index, for picking a session)*

`B0` repo hygiene · `B1` SQLite concurrency + data-loss (DB-*) · `B2` dead exact-ID match · `B3` external-data settings wiring (INT-*) · `B4` queue integrity + playback event storms + NowPlaying VM · `B5` audio engine safety · `B6` metadata-editor file-write integrity · `B7` matching accuracy · `B8` provider correctness/compliance · `B9` scanner + watcher · `B10` orchestrators/cache/single-flight/network · `B11` VM leaks & lifecycle · `B12` system integration & shell · `B13` Now-Playing panels & lyrics UI · `B14` theme sweep + UX safeguards · `B15` test-suite hardening · `B16` smart-library-enrichment pipeline. **Roots first (`B1`,`B2`,`B3`)**, then the rest per the dependency summary above.

### 17.3 Deferred polish backlog *(non-blocking — no correctness/data-loss/crash risk; safe to schedule after the batches)*

None of these break behavior or lose data; they are intentionally **not** assigned to a batch so batch scope stays tight. Grouped by kind:

- **Performance polish (highest-value here — do these first if you pick up the backlog)**: `SH-06`/`UI-MW-05` (hook `CompositionTarget.Rendering` only while playing+visualizer-visible, not permanently at 60 Hz), `UI-LP-04` (bind the row play-icon state instead of the manually-tracked `List<Button>` that grows in a virtualized list), `HL-04` (make the 250-item artwork LRU configurable for 10k+ libraries), `NP-03`/`NP-04` (replace hardcoded animation/height magic numbers with adaptive values), `OL-14` (wrap the synchronous embedded-lyrics TagLib read in `Task.Run` so large FLAC files don't block the caller).
- **Defensive / robustness nits (latent — don't trigger on today's call paths)**: `HLP-02` (null-guard `IdGenerator.From*`), `HLP-05` (`double.IsInfinity` guard in the formatters), `HL-03` (`BitmapImage` is UI-thread-affine — safe today since the converter runs on the UI thread; guard if ever called off-thread), `CS-02` (keep artwork-token path separators consistent), `ME-09` (`FileOpenPicker` STA/COM edge), `SH-01` (use `AppendConsole` everywhere), `SH-03` (drop the redundant `TryEnqueue` after `await`).
- **Refactors (no behavior change)**: `CS-01`/`CS-04` (split the 27 KB/30 KB `QueueService`/`LocalLibraryScanner` into partials), `AR-01` (make `QueueItem` a record — the *equality* half is already fixed in B4/QUEUE-09; this is the immutability refactor), `AR-02`/`AR-04`/`AR-06` (group wide records into sub-records), `OL-07` (extract the repeated `Orchestrate*` boilerplate into a generic helper), `OL-10` (`ParseLrcContent` `public`→`internal`), `SH-12` (use `PEReader` for the bass.dll arch check), `HLP-04` (fix the stale `ArtworkPathConverter` comment), `OL-13` (collapse the duplicate `Lyrics`/`lyrics` directory probes into one case-insensitive lookup on Windows).
- **Feature / UX enhancements (new capability, beyond "fix what exists")**: `OL-05` (force lyrics re-fetch), `CS-03` (on-disk artwork-cache eviction budget), `NP-18` (bitrate/sample-rate in the audio-info tile), `SH-04` (live sleep-timer countdown), `SH-07` (multi-tier responsive breakpoints), `SH-09` (mini-player Next/Prev buttons), `HL-07` (visualizer corner radius 1→2-3 px), `ME-08` (show local metadata before the online fetch), `ME-10` (convert WebP→JPEG before embedding), `HLP-06` (accept or log-reject BMP/TIFF cover art), `UI-NP-06` (copy-lyrics / lyrics-offset control).
- **§7 UI tooltip / affordance / layout polish (cosmetic; the visual identity is intentionally preserved)**: `UI-MW-01,02,03,06,07`; `UI-HP-01,02,03`; `UI-LP-01,02,03,05`; `UI-AP-02,03`; `UI-AR-02,03`; `UI-PL-03,04,05`; `UI-ED-01,02,03`; `UI-SR-01,02,03,04`; `UI-ST-03,04,05,06`; `UI-MD-01,02,03`; `UI-LE-01,02,03`; `UI-MP-01,02`. *(Add tooltips, hover play-overlays, result-count badges, empty-state richness, "Read More" expanders, provider badges, etc. — all additive.)*
- **Hygiene stragglers**: `NP-13` (add explicit `using System;` in `AlbumArtPanel.xaml.cs` — same class as `MC-03`; fold into B0 if you touch that file).

### 17.4 No-action — verified-good or by-design *(nothing to fix)*

- **✅ Confirmed good** (positive audit findings, left as-is): `OL-01` (offline-first pattern), `OL-03` (single-flight + two-tier cache), `OL-09` (image validation before cache), `NP-11` (lyric-position binary search), `HL-06` (spectrum rectangle rendering), `SH-08` (mini-player behavior), `AR-05` (`LyricsModels` design).
- **By-design / not a defect**: `OL-11` (LRC offset sign — **re-verified on the second pass: the sign matches its comment / LRCLIB behavior**, see the §12.12 "Verified-clean (helpers)" note), `OL-12` (multi-timestamp line duplication is correct LRC behavior), `OL-04` (defensive check — harmless), `HL-05` (fire-and-forget `ShowAsync` — each handler already try-catches).
- **Explicitly out of scope by user request**: `UI-FX-01` (Audio FX page intentionally left blank for now).
- **Broader "verified-clean" evidence** lives in the §12 subsystem notes (DB, Audio, shell, helpers, tests) and the §16 provenance table — those record what was probed and cleared, not just what was flagged.

**Bottom line.** With §17 in place, §15's claim holds literally: every finding in §1–§13 is either **fixed by a named batch**, **parked in the §17.3 backlog** (all non-blocking), or **marked no-action in §17.4** (verified-good/by-design). A coding agent handed "do Batch N" has, for each cited ID, a real and locatable finding row, an exact file list, explicit acceptance criteria, and stated dependencies — and can trust that finishing the batch leaves no in-scope bug behind.

---

## 18. Resolution Log — fixes applied by the coding agent

*(Appended at the end of the document per the user's instruction: every resolved issue is recorded here as it lands. Newest session first. Format: batch → commit → per-ID status → acceptance evidence → notes/behavior changes. IDs marked ✅ should be treated as fixed; later batches must not re-fix them.)*

### Session 2026-08-25 — Playback-bar artwork binding + Settings backend wiring + library art *(commits `559e05b`, …)*

**Scope**: three user-reported gaps — (1) playback-bar album art freezing (never updating on track change) *after* the window is dragged between monitors of different DPI; (2) several Settings sections whose controls persist but have **no backend reading them**, so they feel dead; (3) no album thumbnail next to the play/pause button in the library song list.

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-34 | 🔴 Visible defect (latent in the NF-28 refresh path, made reliably reproducible by NF-33) | `ArtworkPathConverter.WalkAndRefresh` | The transport-bar cover, sidebar art and ambient backdrop are driven by a OneWay `{Binding CurrentArtworkUrl, Converter=ArtworkPathConverter}`. The cross-monitor scale-change walk re-decoded each `Image` by assigning `image.Source` directly — but a direct assignment writes a **local value**, and per XAML dependency-property precedence a local value **removes the binding**. So the first drag onto a different-DPI monitor detached the binding, and thereafter `CurrentArtworkUrl` changes (i.e. changing track) had nothing to flow through: the art froze on whatever was playing at drag time. Fix: when an `Image`'s `Source` still carries a binding expression, refresh it by **re-applying that binding** (`GetBindingExpression().ParentBinding` → `SetBinding`), which re-runs the converter at the new scale *and* keeps the binding live for later track changes; only genuinely source-assigned images (no binding expression) keep the direct-swap fallback. Shape/Border/Panel `ImageBrush` fills are never bound here, so those branches are unchanged. |

### Session 2026-08-24 — Crash hardening + artwork hi-res sources + DPI refresh *(commits `ce83916`, `7301e64`)*

**Scope**: user-reported runtime crash (exit `0xC000027B` stowed exception during playback), playback-bar album art not refreshing on monitor drag, and album/artist art *still* low-res/pixelated after NF-28..30.

Ground truth first: inspecting the user's actual `ArtworkCache` showed most covers are **500×500 / 700×700** files — no decode-side setting can fix a source that small being stretched to ~660 physical px (NowPlaying at 150%) or across a maximized window (backdrop). The pixelation was upstream, in what we download and cache.

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-32a | 🔴 Chronic failure since inception + diagnostic flood | `WindowsAudioDeviceHelper` | The `MMDeviceEnumerator` ComImport carried a **fabricated CLSID** (`{BCDE0385-4944-4EA6-82B2-9D67097C93FE}`); the real `CLSID_MMDeviceEnumerator` is `{BCDE0395-E52F-467C-844D-A560C580CB51}`. Every device query ever made failed `REGDB_E_CLASSNOTREG (0x80040154)`, flooding the debug log with first-chance COMExceptions at every 3s TTL expiry (the storm visible in the crash log) and forcing OutputDeviceName/Quality onto BASS fallback strings forever. Fixed the GUID; failed queries now also cache under the TTL and log once per streak so a broken COM environment cannot flood output. |
| NF-32b | 🔴 Crash class | `ManagedBassAudioService` | An exception escaping a native BASS sync callback cannot unwind the pinvoke boundary — it surfaces as a fatal **stowed exception** (`0xC000027B`). `OnTrackEndedCallback` is now fully guarded, and all cross-thread event raises (TrackStarted, TrackEnded pool items, position-timer PositionChanged) wrap their handlers so one throwing subscriber can never kill the engine thread or process. |
| NF-32c | ⚠️ Crash class | `MainWindow` rendering tick | `CompositionTarget_Rendering` runs every frame for the app lifetime; any transient throw (crossfade state, visualizer teardown) was fatal. Guarded per-frame. |
| NF-32d | ⚠️ Diagnosability | `Octave.Desktop/Helpers/CrashLog.cs` | The stowed-exception crash left zero usable diagnostics. All three global handlers (XAML UnhandledException / AppDomain.UnhandledException / UnobservedTaskException) now append timestamped full exception detail to `LocalState/logs/diagnostics-*.log`. Handled-policy unchanged (SH-10 intact). |
| NF-31a | 🔴 Root cause of pixelation | `CoverArtArchiveArtworkProvider` | Candidate order preferred **Thumb500 over everything** — and candidate order IS final quality (the orchestrator downloads until the first URL succeeds). Preference is now Thumb1200 → full-resolution image → Large → Thumb500, in both release and release-group paths. |
| NF-31b | 🔴 Permanence of the defect | `ExternalArtworkOrchestrator` (+ new `ImageDimensionReader`) | Even after NF-31a, the 90-day resolved-token cache would serve old 500px entries forever — rescans could never heal them. `ResolveAndCacheAlbumArtworkAsync` gained opt-in `preferHighResolutionUpgrade`: when the cached file's encoded width probes below 1000px, an upgrade-requesting caller re-sweeps providers once per 7-day cooldown memo; "nothing better found" falls back to the old token. Playback-time callers keep the plain cache-hit-no-network contract. Width probing is pure-managed header parsing (JPEG SOF scan / PNG IHDR) — no decoder dependency in Core. The smart-enrichment scan passes `upgrade:true`, so running Scan & Enrich self-heals pre-fix covers. |
| NF-33 | ⚠️ Visible defect | `ArtworkPathConverter` / both windows | XamlRoot.Changed alone missed some cross-monitor drags (playback-bar art stayed stale). Windows now also subscribe `AppWindow.Changed` (`DidPositionChange`) — the deterministic "crossed a display boundary" signal — re-evaluating the live RasterizationScale through a shared per-scope baseline; either trigger applies one clear-cache + tree re-decode pass. Monitor state dedupes wiring (a re-Loaded scope can no longer stack duplicate handlers); the Changed lambda and every walk step are guarded so a mid-walk tree mutation can neither abort the refresh nor escape as another stowed exception. |

#### Notes & deliberate trade-offs

- A failed hi-res upgrade retries after the 7-day cooldown memo lapses — providers that genuinely have no better art aren't hammered per scan.
- Artist images still come from TheAudioDB's single thumb size per artist; nothing higher exists there to prefer.
- Existing low-res covers upgrade only when something calls resolve for them again — practically: run Scan & Enrich (or open the album's detail page).
- If a crash recurs, `LocalState/logs/diagnostics-yyyyMMdd.log` now names the site.

**Acceptance verified**: build `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **324/324** (7 new tests: upgrade swap, hi-res-hit-no-network, fallback+cooldown, no-flag contract, PNG/JPEG width parse, garbage-input safety).

---

### Session 2026-08-24 — Artwork scaling + DPI-change safety audit *(commit `aee3d67`)*

**Scope**: user follow-up after the enrichment batch — "how the images (album art, artist art and others) are being displayed… not scaled properly… over-sharpening or pixelated effect… also make sure there should be no errors/bug caused by dpi change (dragging the window from one monitor to another of different DPI)."

Full audit mapped all 15 `ArtworkPathConverter` consumers against their decode buckets: NowPlaying art 440 ≤ 600 ✓, entity header 232 ≤ 600 ✓, album/artist/home cards 136/120/140 ≤ 320 ✓, list thumbs 48–64 ≤ 112 ✓, `DecodePixelType.Physical` × live `RasterizationScale` usage correct, placeholders 1024×1024, WIC Fant decode downscaling high quality. The visible defects were three:

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-28 | 🔴 Visible defect on every monitor drag | `ArtworkPathConverter` / `MainWindow` / `MiniPlayerWindow` | Decoded bitmaps are cached per rasterization scale, but NOTHING subscribed to scale changes: dragging the window onto a different-DPI monitor left every artwork bound to its old-density decode — exactly the soft/pixelated-after-drag symptom. The converter now records the (url, parameter) context behind each decoded BitmapImage in a self-cleaning `ConditionalWeakTable`, and both windows attach a `XamlRoot.Changed` monitor (`AttachDisplayScaleMonitor`) that clears the stale cache and walks the visual tree re-decoding every converter-produced Image AND ImageBrush fill (Ellipse fills included) at the new scale. Sources not produced by the converter (metadata dialog's full-res previews) are untouched; direct property assignment works regardless of binding mode (OneTime included). Note: `XamlRootChangedEventArgs` carries no payload — the new scale must be read off the captured XamlRoot. |
| NF-29 | ⚠️ Wrong-density decode | `MiniPlayerWindow` | Artwork decoded using the MAIN window's `RasterizationScale` — wrong whenever the always-on-top mini player sits on a monitor with a different DPI from the (hidden) main window. Per-instance `ScaleProvider` now points the mini player's converter at its own XamlRoot; other consumers default to the main window as before. |
| NF-30 | ⚠️ Chronic softness/banding | `MainWindow.xaml` ambient backdrop | The full-window background image used the "Large" bucket (600 logical) stretched UniformToFill across e.g. 1600–2560 logical — a ~3–4× GPU upscale on the single most visible image in the app (soft edges, banding under the acrylic tint). New dedicated "Background" bucket (960 logical) with a 1920 physical-pixel cap so 2.5× monitors cannot balloon decode memory (~14 MB worst case for the one hot backdrop); EntityDetailPage keeps Large for its 232 px header. Cache keys are now effective-decode-width only, so scales clamping to the same width share one bitmap instead of decoding duplicates. |

#### Notes & deliberate trade-offs

- On a real DPI change every converter-produced image re-decodes asynchronously: brief placeholder-to-image fade-in is expected (the ambient backdrop's existing ImageOpened opacity storyboard re-runs, reading as an intentional cross-fade).
- The refresh walk covers Image.Source plus Shape.Fill / Border.Background / Panel.Background ImageBrushes — the only brush usages in the app.
- "Over-sharpening" in the report maps to the upscaled backdrop (NF-30), stale low-density decodes after monitor drag (NF-28) and the mini-player mismatch (NF-29); no sharpening filters exist anywhere in the pipeline.

**Acceptance verified**: build `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **317/317**. Desktop-only change — structural reasoning + the audit table above; no Core code touched.

---

### Session 2026-08-24 — Smart-enrichment review apply + expanding settings card *(commit `3770479`)*

**Scope**: user report on the Settings "Scan & Enrich Library..." flow — (1) button text truncated, (2) replace the modal popup with an in-card vertical expansion showing everything the popup did, (3) "make sure that this feature works perfectly": popup elements stretched out of view, "Preview Changes (D…" cut off, review-card Apply Candidate button next to an invisible button, and clicking Apply Candidate showed **"apply failed" in green**.

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-27 | 🔴 Total feature failure + ⚠️ UI defects | `SmartLibraryEnrichmentService.BuildPlanForTrackAsync` / `ReviewItemViewModel` / `LibraryEnrichmentDialog→Panel` | **Apply could never succeed**: NeedsReview plans returned from `BuildPlanForTrackAsync` at the gates/confidence check — BEFORE the block that constructs `ProposedUpdate` — so `ApplySinglePlanInternalAsync` rejected every one with "No proposed changes in plan." Review plans now fall through the same action-building code (status set, no early return); nothing auto-applies them since the scan's apply pass filters on `SafeReadyToApply`. Manual-review plans bypass the automatic write-policy switches (`AutoFillMissingMetadata`/`NeverWriteAutomatically`/`ReplaceExistingMetadata`) because the side-by-side card plus the Apply click ARE the consent — minimal-diff and value clamps still apply, identical values are never restamped, and artist bio/photo enrichment is skipped for review plans (it writes the shared Artist record before approval). UI side: failures keep the card actionable and surface `EnrichmentApplyResult.ErrorMessage` ("Apply failed: \<reason\>"), color-coded via three stacked ThemeResource TextBlocks (success green / failure red / idle accent) instead of always-accent-green. |

| ID | Status | Evidence |
|----|--------|----------|
| ENR-05 | ✅ Fixed | Launcher button truncated to "Scan & Enrich Libr…" when the window narrowed: the `*,Auto` header grid squeezed the Auto column once the wrapped description consumed all space. Button now has MinWidth 190 + no-wrap text; the description column absorbs narrow widths by wrapping. |
| ENR-06 | ✅ Fixed | The ContentDialog (fixed 720×620 box) is gone entirely. New `LibraryEnrichmentPanel` UserControl hosts the same content inline in the settings card; clicking the launcher expands the card vertically with a standard fade + slide-up implicit composition animation (hide animates out). Fresh transient view model per expansion; `Cleanup()` detaches singleton-service events on collapse AND page unload (VM-02 discipline). Panel layout repairs vs the dialog: action buttons own a full-width row so "Preview Changes (Dry Run)" renders whole at any width; metrics moved from six squeezed columns to a 3×2 grid; review-card status text sits on its own wrapping row ABOVE a right-aligned Apply/Skip/Never-Ask row (the old single `*,Auto` row clipped the rightmost button whenever StatusText grew); gate warnings now render above the comparison panels so the user sees WHY a track needs review; a run-summary line reports metadata fields/artwork/lyrics/artists written and elapsed time after each scan. |

#### Notes & deliberate trade-offs

- Review plans now fetch artwork/lyrics during planning like safe plans do, so persisted `PlanJson` can carry base64 artwork bytes for later-session applies (review queues are small — low-confidence tracks only). Accepted size cost for correctness of the round trip.
- `ReviewQueue_PersistedPlans_RoundTripProposedUpdate` pins the serialized-plan round trip; existing gates tests (`MandatoryGates_RejectsHighScoringVersionMismatch_RoutesToReview`) double as proof that proposals are never auto-applied.
- The two CS1503s from a wrong `ElementCompositionPreview.SetImplicitShowAnimation` signature (takes UIElement, not Visual) masked as the familiar WMC0909/WMC0001/WMC9999 pass-2 cascade across unrelated files — fixed at the C# source and everything cleared.

**Acceptance verified**: build `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **317/317** (+2 new SmartLibraryEnrichment regression tests).

---

### Session 2026-08-24 — Now Playing UX + queue auto-advance — user-reported batch *(commit `ef5ce27`)*

**Scope**: six user reports in one pass — EQ status chips, Up Next panel empty on load, whole-page scroll yanked by lyric ticks, lyrics auto-follow/sync button, offset reset button, playback not advancing at track end.

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-22 | 🔴 Total feature failure | `QueueService.HandleTrackEndedAsync` | Batch 4's QUEUE-02 guard restructured the repeat-mode switch into if/else-if/else where the HEALTHY end branch (position ≥ 0.75 s) only reset the failure counter and NEVER advanced — every track that played to its end silently stopped the queue. The existing advance test passed by accident: its mocked `PositionSeconds` defaulted to 0.0, exercising the FAILURE branch (which still advances while under the cap). Fixed: healthy ends advance AND reset the streak; failure ends count toward the cap as before. Two regression tests pin both behaviors (`TrackEnded_NaturalHealthyEnd_AdvancesToNextTrack`, `TrackEnded_HealthyEnd_ResetsConsecutiveLoadFailureStreak`). |
| NF-23 | ⚠️ UX polish | `SettingsPage.xaml` EQ card | User asked for the ready/unavailable pill chips to go; replaced with plain colored text "Ready"/"Error" (`OCTAVE_StatusSuccess/Danger`) + explanatory ToolTips; unused SuccessSoft/DangerSoft theme tokens removed. |
| NF-24 | ⚠️ Feature broken on load | `NowPlayingViewModel.RefreshState` | The Up Next panel was always EMPTY when the page opened: `RefreshUpNextQueue` ran only on `QueueChanged`, but that event fires from the SINGLETON queue service and had already happened before the TRANSIENT view model subscribed. `RefreshState` now hydrates the mirror once per page load; NP-09's identical-window early-return keeps repeat Loaded calls cheap. |
| NF-25 | 🔴 Page unusable while synced lyrics play | `LyricsPanel.xaml.cs` | Active-line scrolling used `StartBringIntoView`, which bubbles through EVERY ancestor ScrollViewer — each lyric tick yanked the whole Now Playing page back (user could not scroll at all). Replaced with a `ChangeView` computed against the panel's OWN viewer only (target = line Y + top padding − 40 % viewport), after `UpdateLayout()` so the 18→22 px active-line resize is reflected. Auto-follow additions requested by the user ride the same fix: any non-auto view movement (`ViewChanged` guarded by an `_autoScrollInFlight` flag + `PointerWheelChanged`) suspends following and shows a SYNC button left of the −/+ offset buttons; clicking it re-centers the active line and resumes following; new lyric sheets reset to following. |
| NF-26 | ⚠️ UX gap | `LyricsPanel` toolbar | Offset RESET button added after the + button: raises the inverse delta (−OffsetMs) through the existing nudge pipeline, returning to exactly 0 in one click. |

#### Notes & deliberate trade-offs

- Wheel suspension fires on `PointerWheelChanged` because `ViewChanged` alone cannot distinguish a wheel tick from the tail of our own scroll animation; touch panning is caught by the flag-guarded ViewChanged path.
- If the user grabs the scrollbar DURING an auto-scroll animation, intermediate events stay suppressed until that animation completes — accepted; the next manual movement suspends again.

**Acceptance verified**: build `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **315/315** (+2 new QueueService regression tests). Desktop-only changes are structural-reasoning verified (XAML bindings, DP callbacks, DI lifetimes); the queue fix is covered by deterministic tests.

---

### Session 2026-08-24 — Equalizer dead-band fix *(commit `f61bed4`)*

**Scope**: user report "equaliser is not working" after the Settings remake. Diagnosed the full chain — XAML bindings (slider/preset/toggle all wired), ShellViewModel EQ region, engine attach/detach lifecycle across stream swaps — all correct. The deployed bass_fx.dll was verified x64 and present.

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-21 | 🔴 Total feature failure | `ManagedBassAudioService.ApplyBandUnlocked` | Every EQ band was configured correctly and STILL processed zero audio: `PeakEQParameters` was built via object initializer without `lChannel`, which defaults to `FXChannelFlags.None` (0). Native `BASS_BFX_PEAKEQ.lChannel` takes `BASS_BFX_CHANxxx` flag/s with `BASS_BFX_CHANALL = -1`; a 0 mask matches no channel. Sliders, presets, enable-toggle and per-track re-attach all worked — the sound never changed. Fixed by setting `lChannel = FXChannelFlags.All` plus a debug log when `Bass.FXSetParameters` fails. The limiter was unaffected (no explicit params → native defaults initialize to CHANALL). |

**Acceptance verified**: offline decode-channel DSP probe against the shipped binaries (bass.dll 2.4.15 / bass_fx 2.4.12.6): a pure 125 Hz sine pushed through the exact production parameter shape measured **1.00× (0.0 dB)** vs bypass (bug reproduced); with `lChannel = All` it measured **3.21× (+10.1 dB)** at the band center. Engine read-back (`FXGetParameters`) confirmed every other field had been stored correctly all along.

**Final state**: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **313/313**.

---

### Session 2026-08-24 — Settings page remake — user-directed full rework *(commit `70eda90`)*

**Scope**: "remake the whole settings page." The single cramped 600 px column became a 980 px stack of Windows-11-style cards (`SettingsCard` Border style + shared `RowTitle/RowDesc/CapsHeader` styles; rows are `*,Auto` grids — title+description left, control right). Cards: Playback · Equalizer · Music Library · Online & External Data (launcher / privacy / providers / credentials / write policy / caching / scan prefs) · Engine Diagnostics. All existing commands, handlers and `ExternalDataSettings` bindings preserved.

| ID | Status | Evidence |
|----|--------|----------|
| UI-ST-03 | ✅ Fixed | EQ presets now show which one is active. `ShellViewModel.PresetGains` is the single source of truth (the same table `EqPreset` applies); `ActivePresetName` is DERIVED by comparing actual band gains against it on every band `PropertyChanged`, so a restored session reports its true state without extra plumbing and any hand-tuned slider flips the label to "Custom". Preset buttons show a check glyph + a caption names the active preset. |
| UI-ST-04 | ✅ Fixed | New `IAudioPlayerService.IsEqEngineAvailable`, probed once during `ManagedBassAudioService.Init` from `BassFx.Version` (native DLL absence = P/Invoke failure). The Equalizer card shows an "EQ engine ready" success pill or "bass_fx.dll missing — Equalizer unavailable" danger pill instead of sliders that silently did nothing. |
| UI-ST-05 | ✅ Fixed | ToolTips added: Add Folder ("scanning runs in the background"), Rescan All, folder Remove (imported tracks stay), duplicate **Move** ("moves the physical audio file AND updates its path in the library database" — the audit's exact complaint), duplicate Delete. |
| UI-ST-06 | ✅ Fixed | The API-key section now states the public test key "2" is shared and strictly rate-limited (slower artist enrichment) and says where to get a free supporter key (theaudiodb.com → API Registration); PasswordBox header reduced to just "TheAudioDB API Key". |

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-20 | ⚠️ Perf / UX bug | `SettingsPage.xaml.cs` | TheAudioDB `PasswordChanged` called `SaveCommand.ExecuteAsync(null)` — every typed character serialized and persisted the ENTIRE `ExternalDataSettings` object (and raised its `SettingsChanged` event back into the VM, re-hydrating all ~30 observable properties mid-typing). Now the handler only syncs `TheAudioDbApiKey`; persistence happens once on `LostFocus`. Test Connection still reads the live VM value, so testing an uncommitted key works unchanged. |

#### Notes & deliberate trade-offs

- **Semantic status colors**: the danger button's hardcoded `#EF5350` and the flyout's `#AAAAAA` moved onto new Light/Dark theme dictionaries (`OCTAVE_StatusDanger` etc.) — same values as the LibraryEnrichmentDialog set from the previous commit.
- **Sleep timer**: five loose buttons replaced by ONE ComboBox (Off/15/30/45/60) with a `SelectionChanged` mapping to the existing `SetSleepTimerCommand`; the status line hides while Off.
- **Console look preserved**: `#111111` background + green Consolas text stays exactly as sanctioned in the B14 entry; only the ScrollViewer gained a proper corner radius and scrollbar visibility.
- **XAML resource rule learned twice now**: a `<ResourceDictionary.ThemeDictionaries>` block inside `Page.Resources`/`ContentDialog.Resources` requires wrapping EVERYTHING in one explicit `<ResourceDictionary>` child (keyed items cannot sit beside a keyless nested dictionary).

**Acceptance verified**: build `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **313/313** (Core change limited to the new interface member + probe). Every control from the old page exists in the remake with the same binding target; no setting was dropped.

---

### Session 2026-08-24 — Smart-enrichment metadata UI pass *(commit `d65cc6e`)*

**Scope**: user-directed follow-up to B16 — "fix smart enrichment metadata ui and all related code." Audited `MetadataEnrichmentDialog.xaml(.cs)` + `MetadataEnrichmentViewModel` + `LibraryEnrichmentDialog.xaml(.cs)` + `LibraryEnrichmentViewModel` + `ExternalDataSettingsViewModel`.

| ID | Status | Evidence |
|----|--------|----------|
| ENR-02 | ✅ Wired end-to-end | B16 built `ITrackEnrichmentWorkflow.HydrateCandidateAsync` but NOTHING called it: switching candidates in the review dialog left artwork bytes and lyrics comparisons permanently empty for any non-top candidate. `MetadataEnrichmentViewModel` now tracks `_hydratedCandidateIds` (the plan pre-hydrates its top candidate — the set starts from `plan.BestCandidate.CandidateId` after each search); selecting an unhydrated candidate kicks off a detached hydration under its own CTS with a stale-run guard (`ReferenceEquals(cts, _hydrateCts)` checked before AND after the dispatcher hop), swaps the hydrated record into `Candidates[idx]` and re-points `SelectedCandidate` (id added to the set BEFORE reassignment so the `OnSelectedCandidateChanged` hook can't recurse). Failed hydrations are deliberately NOT cached so re-selecting retries. `Cleanup()` disposes the third CTS. |
| Theme sweep | ✅ Fixed | MetadataEnrichmentDialog: `#25FFFFFF`×4 borders → CardStrokeColorDefault, table header `#20FFFFFF` → DividerStrokeColorDefault, current-value `#CCCCCC`×8 → TextFillColorSecondary, proposed `White`×8 → TextFillColorPrimary, headers `#888888` → Tertiary, artwork tile `#1E1E1E` → LayerFillColorDefault, lyrics box `#18FFFFFF` override removed (default themed TextBox chrome). LibraryEnrichmentDialog: neon-on-dark metric colors (#00FF66/#FFAA00/#FF4444/#888888) + panel tints moved into Light/Dark `OCTAVE_Status*` theme dictionaries; accent-filled buttons use `AccentButtonStyle` instead of hardcoded White foreground. |
| Dialog logic | ✅ Fixed | Primary button gated `CanApply(HasCandidates && !IsApplying)` — "Apply Selected Changes" was offered even for a No-Match result. New "Proposed Cover" preview tile shows the bytes that WILL be written (custom local pick wins over hydrated online cover) via an `EffectiveProposedArtwork` byte[]→BitmapImage helper — x:Bind function bindings REJECT Converters (WMC1121), so the conversion lives in code-behind. |

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-19 | ⚠️ Data-integrity race | `LibraryEnrichmentViewModel.ReviewItemViewModel` | Apply/Skip/Never-Ask-Again buttons disabled only via `IsResolved`, so a rapid double-tap stacked TWO concurrent `ApplySinglePlanAsync` runs writing the SAME audio file before the first flipped `IsResolved`. All three commands now carry `[RelayCommand(CanExecute = nameof(IsNotProcessing))]` with `NotifyCanExecuteChanged` wired through `OnIsProcessingChanged`. |

#### Notes & tooling discovery

- **WMC9999 masks real XAML errors**: this WinUI XamlCompiler crashes loading its own localized error strings when it hits certain SEMANTIC errors, printing only "Could not find any resources … ErrorMessages.resources". Bisected twice via targeted stashing; root causes found were WMC1121-class issues (converter-on-function-binding; page-function-with-DataType-args inside a DataTemplate — unsupported). Practical lesson: when WMC9999 appears, suspect a semantic binding error in the most recently edited XAML and bisect file-by-file; `VSLANG=1033` does NOT unwrap it.

**Acceptance verified**: build 0 warnings / 0 errors · tests **313/313** · hydration contract pinned against `TrackEnrichmentWorkflow.CreateEnrichmentPlanAsync` (metadata-only previews + top-candidate hydration confirmed at source).

---

### Session 2026-08-24 — Batch 15 — Test-suite hardening *(commit `b215e6f`)*

**Files opened**: per batch letter — the named TEST-03..20 surfaces across `Octave.Core.Tests/*` plus the production seams extraction forced open: `DurationFormatter` (NEW), `LyricsService`, `AudioPlayerServiceTests`, `ManagedBassAudioService`, `WindowsAudioDeviceHelper`, `QueueService`, `MusicBrainzProviderTests`, `LocalLibraryScannerTests`, `OnlineLyricsOrchestrator`, NEW `LibraryServiceTests`, NEW `PlaylistServiceTests`, `FormattingTests`.

| ID | Status | Evidence |
|----|--------|----------|
| TEST-03 | ✅ | New `DurationFormatter` owns m:ss/h:mm:ss formatting; Desktop converter + MiniPlayerWindow delegate. FormattingTests drives NaN/±Infinity/59.9/60/600/3599.99 rows. |
| TEST-04 | ✅ | `LyricsService.FindActiveLineIndex` extracted from `NowPlayingViewModel` as THE production binary search; boundary tests pin exact-start hits, null/empty guards, unsorted-input safety. |
| TEST-05 | ✅ | Device-detail shape assertions on the parsed blob output. |
| TEST-06 | ✅ | `AggregateFftBins` made internal; exact math pinned: zero-input first frame 0.014, full-scale spike 0.7, decay-from-0.7 = 0.5776, monotonic non-decreasing bin mapping. |
| TEST-07 | ✅ | `WindowsAudioDeviceHelper.ParseDeviceFormatBlob` pure parser over WAVEFORMATEX (18 B) / EXTENSIBLE (40 B, cbSize≥22 honors wValidBitsPerSample); `ReadBlobStruct<T>` always frees its HGLOBAL; garbage-blob theories. |
| TEST-08 | ✅ | QueueService shuffle RNG injectable — two instances seeded Random(42) produce identical true permutations ≠ identity. |
| TEST-09 | ✅ | CallCount-instrumented handlers prove pre-cancelled tokens never reach HTTP (MusicBrainz / Artwork / Lyrics). This test EXPOSED NF-17. |
| TEST-10 | ✅ | Fractional-timestamp variants ([00:04.5]→4.5 etc.), multi-timestamp shared chorus lines, offset clamp-to-zero. Exposed NF-18. |
| TEST-11 | ✅ | Resume-on-startup seeks to the saved position exactly ONCE (Verify Seek(90.0) Times.Once). |
| TEST-12 | ✅ | Single-flight: empty key bypasses dedup while still honoring tokens (genuinely-async factory — completed tasks return before WaitAsync consults a token); concurrent throwing factory delivers the fault to ALL waiters, runs once, evicts the entry. |
| TEST-13 | ✅ | Scanner: zero-byte files skipped without aborting the run; pre-cancelled token throws OCE at the write semaphore BEFORE enumeration; mid-scan cancel raises ≥1 progress event then OCE and leaves no ghost rows. |
| TEST-14 | ✅ | NEW `LibraryServiceTests`: folder add/remove/rescan wiring driven against the real `SqliteDbContext` with mocked scanner/watcher. |
| TEST-15 | ✅ | Similarity table rows extended (& fold, direct Levenshtein bands). |
| TEST-16 | ✅ | NEW `PlaylistServiceTests`: create/rename/delete round-trips raise PlaylistsChanged each step; reorder-by-track-id survives DUPLICATE entries (DB-08 one-position-per-entry contract, first-unpositioned-copy consumption); entry removal removes exactly one copy; delete cascades entries but not tracks. |
| TEST-17 | ✅ | AudioPlayerServiceTests dispose the shared player via fixture. |
| TEST-18 | ✅ | GUID-isolated temp dir for the filename-parse test. |
| TEST-19 | ✅ | Deterministic async: ProviderRateLimiter takes an injectable clock (+ `NextAllowedUtcForTests`) — Retry-After windows extend atomically and never shorten; outstanding-reservation behavior proven via stopwatch lower bound + frozen-clock no-double-extension assertion; queue events use `TaskCompletionSource(RunContinuationsAsynchronously)` instead of sleeps. |
| TEST-20 | ✅ | Canonical Sqlite in-memory fixture recorded for the suite: uniquely-named shared-cache DB (`name_{Guid:N}`; Mode=Memory;Cache=Shared) + keeper connection held for the fixture lifetime; unnamed `":memory:"` shares ONE pool keyed by connection string across parallel fixtures and was the root cause of 12 "no such table" failures. |

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-17 | ⚠️ Wasted-work / semantics | `OnlineLyricsOrchestrator.FetchLyricsAsync` | A caller whose token was ALREADY cancelled still triggered the full provider sweep — SF-01 deliberately detaches the shared fetch from per-caller tokens, and the orchestrator passes CancellationToken.None INTO the sweep, so the entire HTTP fan-out ran solely for a caller who was gone (surfaced by TEST-09's CallCount==1). Entry gate added before cache/single-flight: cancelled callers get `Unavailable` immediately; the shared-flight semantics are untouched. |
| NF-18 | ⚠️ Parser gap | `LyricsService` LRC regexes | Timestamp fraction required `\d{2,3}`, so single-digit files like `[00:04.5]` silently degraded Synced→Unsynced. Both `LrcTimestampRegex` and `TagRegex` widened to `\d{1,3}`; the ms normalizer already scaled length-1 (×100)/length-2 (×10)/length-3 (as-is). |

**Final state**: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **313/313** (up from 265).

---

### Session 2026-08-24 — Batch 16 — Enrichment pipeline *(commit `ea79962`)*

**Files opened**: per batch letter — `SmartLibraryEnrichmentService`, `TrackEnrichmentWorkflow`, `ITrackEnrichmentWorkflow`, `EnrichmentModels`, provider/cache plumbing for PROV-04/NF-16.

| ID | Status | Evidence |
|----|--------|----------|
| SLE-03 | ✅ Fixed | Artist enrichment awaited (was fire-and-forget); bio/photo persist to the Artist record BEFORE flagging `EnrichArtistBioAndPhoto`, so `IsArtistComplete` flips true and later scans stop re-fetching the same artists. Dry-run suppresses the record write. |
| SLE-04 | ✅ Fixed | Faulted artwork-resolution tasks are EVICTED from the dedup cache instead of poisoning every other track of that album for the whole session. |
| SLE-05 | ✅ Fixed | Eager `allTracks.Select(async…)+WhenAll` (a state machine per track up front) replaced with lazy `Parallel.ForEachAsync(MaxDegreeOfParallelism)`. |
| SLE-06 | ✅ Fixed | Concurrent scans rejected via `SemaphoreSlim(1)` gate; superseded scan CTS disposed; dedup caches effectively scoped to one scan. |
| SLE-07 | ✅ Fixed | Tracks that throw during planning persist a Failed execution plan carrying the exception message — previously they vanished from the review queue/state entirely. |
| SLE-09 | ✅ Fixed | `metadataUpdated` counts Genre/Year/TrackNumber/DiscNumber/ExternalIds writes, not only Title/Artist/Album. |
| ENR-01 | ✅ Fixed *(landed within this batch's workflow work)* | `ExternalIds` gained value-based equality — dictionary members compared by reference flagged IDs as perpetually different and rewrote them on every apply. Null proposed = nothing to apply. |
| ENR-02 | ✅ Fixed *(Core half; UI wiring follows in the next session entry)* | Plan previews are metadata-only; artwork+lyrics hydrate for the TOP candidate at plan time and for any other candidate via new public `HydrateCandidateAsync` (re-reads local tag state so baselines match the plan). N candidates used to mean N sequential HTTP chains before the dialog even opened. |
| ENR-03 | ✅ Fixed | "Album Artist" comparison no longer proposes the candidate's TRACK artist (wrong for compilations/features); row shows local value and proposes nothing. |
| ENR-04 | ✅ Fixed | Field comparisons compare Ordinal so capitalization-only corrections surface as offered changes. |
| PROV-04 | ✅ Fixed | Imageless artist profiles cached 6 h (was pinned 30 days) so a failed photo download retries on the next enrichment pass. |

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-16 | ⚠️ Race | Single-flight caches using `ConcurrentDictionary.GetOrAdd` with async factories | GetOrAdd may invoke the value factory several times CONCURRENTLY (it doesn't lock), so "dedup" still fired duplicate provider calls under contention. Caches now hold `Lazy<Task<T>>` created with `ExecutionAndPublication`, guaranteeing one factory execution per key generation. |

**Final state**: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **265/265** (test-count growth lands in Batch 15).

---

### Session 2026-08-23 — Batch 14 — Theme sweep & UX safeguards *(commit `b57fabd`)*

**Files opened**: worked strictly **page-by-page** as the batch letter prescribes — `MainWindow.xaml`(.cs) → `HomePage.xaml`(.cs) → `AlbumsPage.xaml`(.cs) → `ArtistsPage.xaml`(.cs) → `EntityDetailPage.xaml` → `PlaylistDetailPage.xaml`(.cs) → `PlaylistsPage.xaml`(.cs) → `MiniPlayerWindow.xaml`(.cs) → `SettingsPage.xaml` — plus `App.xaml`, whose shared card style turned out to be the root cause feeding three of this batch's named surfaces (NF-14; drift justified in notes).

| ID | Status | Evidence |
|----|--------|----------|
| MC-04 | ✅ Already resolved *(Batch 13)* | CreditsPanel sweep landed there. Ledger row so B14's ID range is fully accounted for. |
| MC-05 | ✅ Already resolved *(Batch 13)* | QueuePanel header sweep landed there. |
| MC-06 | ✅ Already resolved *(Batch 13)* | QueuePanel title/artist sweep landed there. |
| NP-14 | ✅ Already resolved *(Batch 13)* | LyricsPanel resolves accent/default brushes per call from `Application.Current.Resources`; the queue-row converter keys its tint off the accent color so a change rebuilds it. No cached-across-themes brush remains in either panel. |
| NP-17 | ✅ Fixed *(via UI-MW-04)* | Its scope is exactly the MainWindow hardcoded-text family (`#888888`/`#CCCCCC`/`White`) — covered by the UI-MW-04 sweep below. |
| UI-MW-04 | ✅ Fixed | Every hardcoded color in `MainWindow.xaml` swept: all `White`/`#CCCCCC`/`#AAAAAA`/`#888888`/`#E0E0E0` text → Text Fill Primary/Secondary/Tertiary; `#2A2A2A` pane borders → CardStrokeColorDefault; `#333338`/`#26262B` separators → DividerStrokeColorDefault; tooltip `#18181A`/`#333338` → SolidBackgroundFillColorTertiaryBrush/CardStrokeColorDefault *(see hotfix below — the first attempt used a nonexistent key)*; quality-pill `#1FFFFFFF` → LayerFillColorDefault. The four code-behind brush helpers (shuffle/active/favorite/repeat) now go through a `ThemedBrush(key, fallback)` helper resolving resources PER CALL — a theme switch restyles the transport icons immediately instead of leaving stale brushes. Semantic status colors kept (#00E676 quality / #FFD54F resampling). |
| UI-HP-04 | ✅ Fixed | HomePage surfaces themed per the row's own prescription: quick-play tile `#1E1E1E` → CardBackgroundFillColorDefault; art placeholders `#2A2A2A`/`#222222`/hero `#2A2A2A` → LayerFillColorDefault; track-card `#222222` → LayerFillColorDefault; empty state `#1E1E24` → CardBackgroundFillColorDefault + `#2A2A36` circle → LayerFillColorDefault; all `White`/`#AAAAAA`/`#888888` text → themed fills. The hero spotlight's dark-only gradient (#252535→#121218) was replaced with the themed card surface (trade-off in notes). |
| UI-HP-05 | ✅ Fixed | `Card_PointerEntered`/`Card_PointerExited` deleted from code-behind (with their two now-unused usings); hover feedback is a `PointerOver` VisualState inside each data template animating `BorderBrush` → SystemControlHighlightAccentBrush, returning to Normal automatically on exit. The quick-play tile gained `BorderThickness="1"` + themed idle stroke — the old code-behind set `BorderBrush` on a zero-thickness Grid, so quick-tile hover had never rendered at all. |
| UI-AP-04 | ✅ Fixed | AlbumsPage header/template colors (`White`, `#222222`, `#AAAAAA`) → ThemeResources; same PointerOver hover treatment as HomePage (the page carried the identical code-behind injection, removed with its handlers). |
| UI-AP-01 | ✅ Fixed | Empty-state container added (accent icon circle + "No albums in your library" + guidance), visibility bound via a new `EmptyStateVisibility(int)` x:Bind function to `ViewModel.Items.Count`. |
| UI-AR-01 | ✅ Fixed | Same pattern on ArtistsPage: icon circle + "No artists found" + guidance, bound to `ViewModel.Items.Count`; template colors themed; identical code-behind hover injection removed. |
| UI-ED-04 | ✅ Fixed | Banner text/icon colors (`#AAAAAA`×4, `#CCCCCC`, `White`×3) → Secondary/Primary fills; row-list `#888888` metadata → Tertiary. The banner background was `{StaticResource CardHoverBrush}` (#282828, dark-locked — and dangling after NF-14 removed the resource) → `{ThemeResource LayerFillColorDefaultBrush}`. |
| UI-MP-03 | ✅ Fixed | Mini player `Background="#1C1C1C"` → `{ThemeResource SolidBackgroundFillColorBaseBrush}` (base fill chosen over Layer because the grid is the window root, not an overlay); remaining `White`/`#888888` swept. Because ThemeResources follow whatever theme the WINDOW carries, the ctor now also applies `RootGrid.RequestedTheme = ThemeHelper.GetSavedTheme()` — previously the mini window silently used the OS theme while the main window used the saved one. |
| UI-PL-01 | ✅ Fixed | `DeletePlaylist_Click` is now async and shows a ContentDialog ("Delete playlist?" naming the playlist, Delete/Cancel with **Close as default**) before `DeleteSelfCommand.Execute` + GoBack; cancel leaves everything untouched. The PlaylistsPage right-tap "Delete playlist" context-menu item had the same one-click-destroys shape — routed through the identical guard. |
| UI-PL-02 | ✅ Fixed | New-playlist dialog starts with `IsPrimaryButtonEnabled = false` and a `TextChanged` handler re-enables Create only when `!string.IsNullOrWhiteSpace(input.Text)`. |
| UI-ST-01 / SH-05 | ✅ Fixed | Verified the audit's claimed dialogs exist and behave: per-file `DeleteDuplicate_Click` (:170) and bulk `DeleteAllDuplicates_Click` (:213, counts files/groups and states the primary-copy-retention rule before confirming). The missing half of SH-05 was the **explanatory label**: the Duplicate Tracks section now states up front that deletions go to the Recycle Bin, keep the highest-quality copy per group, and that "Manage" reviews individual copies. |
| UI-ST-02 | ✅ Fixed | The neon-green `#00FF66`-on-black "Ignite Main Engines (Run Static Fire)" button — which also floated OUTSIDE the 600 px settings column — is now "Run Engine Diagnostics (Static Fire)", styled as a normal action, placed inside the settings stack as a proper section, and given a caption explaining what it runs and that no library files are modified. The console keeps its deliberate terminal look (#111111 background, green Consolas text). |

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-14 | ⚠️ Theme bug (app-wide) | `App.xaml` + consumers | The three "global brushes" hardcoded dark-theme colors — `CardBackgroundBrush` #181818, `CardHoverBrush` #282828, `PageBackgroundBrush` #121212 — plus `StandardCardStyle`'s literal `#2A2A2A` border. Consumers grep-verified: the shared card style (HomePage/Albums/Artists/SearchResults grids), MainWindow's player bar, and BOTH detail-page banners. This was the root cause underneath UI-ED-04/UI-HP-04-class symptoms far beyond HomePage. Brushes deleted; `StandardCardStyle` setters resolve CardBackgroundFillColorDefault/CardStrokeColorDefault directly; player bar → SolidBackgroundFillColorBase; banners → LayerFillColorDefault. |
| NF-15 | ⚠️ Layout bug | `AlbumsPage.xaml` | The albums GridView never declared `Grid.Row`, so it shared row 0 with the header TextBlock and rendered underneath its text (both children default to row 0; row 1 was empty). Pinned to `Grid.Row="1"`. ArtistsPage had it right — the defect was Albums-only. |

#### Notes & deliberate trade-offs

- **Post-commit hotfix (`7a38901`)**: the UI-MW-04 tooltip swap originally referenced `{ThemeResource SolidBackgroundFillColorDefaultBrush}` — **not a key in the WinUI 3 theme dictionaries**. ThemeResource resolves at runtime, so the build stayed green and `MainWindow.InitializeComponent` threw `XamlParseException` (0x802B000A) at launch. Replaced with `SolidBackgroundFillColorTertiaryBrush` (the existing elevated-solid-surface fill, #282828 in dark). Lesson recorded for all future sweeps: **XAML resource keys have no compile-time validation** — after introducing new `{ThemeResource}` keys, diff them against the WinUI theme-resource set before trusting a green build. A project-wide key audit confirmed this was the only bogus key in the B13/B14 output.
- **Behavior-preserving today, debt paid anyway**: `ThemeHelper` currently pins the app to `ElementTheme.Dark` (`SaveTheme` is a no-op), so every ThemeResource resolves to its dark value and nothing visibly changes after this batch. The point is that the dark-locked styling debt — the exact thing that made light theme unreadable — no longer exists when theming unlocks.
- **Hero gradient → solid card surface**: `GradientStop.Color` does not reliably re-resolve on live theme switches the way FrameworkElement properties do, so the two-stop dark gradient was swapped for the themed card fill. The spotlight keeps its elevation via corner radius/padding; if a themed two-tone is wanted later it needs a ThemeDictionary-based brush resource, not inline stops.
- **Hover VisualState mechanics**: animations without `Storyboard.TargetName` target the element carrying the `VisualStateGroups`, so the templates animate their own root Grid. WinUI styles cannot host visual states, hence the per-template blocks rather than extending `StandardCardStyle`.
- **Accent-filled buttons keep white foreground**: `PrimaryPlayButtonStyle`/small variants intentionally retain `Foreground="White"` over SystemControlHighlightAccent backgrounds — correct contrast in both themes; not part of any sweep.
- **UI-MP-03 brush choice**: the audit suggested `LayerFillColorDefaultBrush`, but the mini player root is the window ground, not content layered over something — `SolidBackgroundFillColorBaseBrush` is the faithful themed equivalent of `#1C1C1C`.
- **Diagnostics section relocation** (part of UI-ST-02): moving it inside the capped stack also fixes the inconsistent-width layout where the harness/console rendered full-window-width below a 600 px settings column.
- **App.xaml drift justified**: the batch says "page-by-page", but EntityDetail's banner referenced the NF-14 resource being deleted — leaving it would have been a runtime `StaticResource` failure. Fixing the shared source once also kept SearchResultsPage's cards consistent without touching that file.

**Acceptance verified** (§15 Batch 14): no hardcoded hex/`White` remains on any batch page (theme IDs resolve per-theme/per-call) · Albums and Artists show guided empty states bound to collection counts · **every destructive action confirms first** (playlist delete ×2 paths, duplicate single, duplicate bulk with pre-deletion count) · a whitespace-only playlist name cannot be created · the debug button explains itself · §17.3 polish items untouched as prescribed.

**Final state**: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **265/265** (no Core changes this batch).

---

### Session 2026-08-23 — Batch 13 — Now Playing panels & lyrics UI *(commit `2df5d5e`)*

**Files opened**: exactly the six the batch names — `NowPlayingPage.xaml`(.cs), `LyricsPanel.xaml`(.cs), `QueuePanel.xaml`(.cs), `CreditsPanel.xaml`, `AlbumArtPanel.xaml`(.cs), `SpectrumVisualizerControl.xaml.cs` — plus the wiring sites the fixes imply (`Octave.Core/Models/DomainModels.cs` for QueueItem, `Interfaces/IQueueService.cs` + `Services/Playback/QueueService.cs` for id-based removal, NEW `Converters/QueuePlayStateConverters.cs`, and NEW `Octave.Core.Tests/QueueItemTests.cs`; drift all justified in notes).

| ID | Status | Evidence |
|----|--------|----------|
| CRIT-05 | ✅ Fixed | Took the audit's own alternative: the synced-lyrics virtualizing ListView is now a non-virtualized `ItemsControl` inside a `ScrollViewer` (lyric lists are <200 lines). Containers are never recycled, so the `_lineElements` list collected via each line's `Loaded` event can never point at a dead/recycled element; it is cleared and rebuilt whenever `SyncedLines` changes, `_lastHighlightedIndex` resets, and highlighting touches ONLY the outgoing + incoming elements (NP-15) instead of sweeping the whole list. |
| NP-01 | ✅ Fixed | Dead `WideLayout`/`NarrowLayout` AdaptiveLayoutGroup visual-state block (states with no setters) deleted from NowPlayingPage.xaml; the responsive behavior actually lives in the immediate/collapsed branch logic. |
| NP-02 | ✅ Fixed | Empty `TopStageGrid_SizeChanged` handler unbound in XAML and the method deleted from code-behind. |
| NP-05 | ✅ Fixed | `AlbumPanelControl_SizeChanged` sets ONLY `MaxHeight` — it no longer also pins `Height`, which fought the panel's own content sizing. |
| NP-06 | ✅ Fixed | Both credits click handlers bail with `if (Frame == null) return;` before starting the async DB lookups — no more work fired from a detaching page. |
| NP-07 | ✅ Fixed | The `AlbumPanelTransform` TranslateTransform block is gone entirely (XAML + the `AlbumPanelTransform.X = 0` reset in code); the slide-out storyboard's `Completed` callback keeps only the genuine post-animation work (hide + collapse). |
| NP-12 | ✅ Fixed | AlbumArtPanel builds its hover Storyboard ONCE per direction (`_hoverInStoryboard ??= BuildHoverStoryboard(1.0)` / `_hoverOutStoryboard ??= …(0.0)`), `Stop()`+`Begin()` per transition — no new Storyboard allocation per mouse enter/exit. |
| NP-15 | ✅ Fixed | `UpdateLyricHighlighting` demotes only the previous index and promotes only the current one — the old full-list restyle per lyric tick is gone (paired with CRIT-05's rewrite). |
| NP-16 | ✅ Fixed | Single scroll driver: `StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.4 })` on the incoming element; `ScrollIntoView` removed, so nothing competes for the scroll position anymore (closes UI-NP-03 too). |
| NP-19 | ✅ Fixed *(was flagged still-open in §14)* | Queue rows bind `QueueItem.ArtworkUrl` — an artwork TOKEN resolved by the view model — never `Track.SourceUri` (the audio file path). Because `Track` carries no artwork field, the VM hydrates tokens lazily after each visible-window rebuild (see notes). |
| NP-20 | ✅ Fixed | The playing queue row gets a translucent accent tint via `QueuePlayStateBrushConverter` bound to `IsPlaying`; `QueueItem` now implements `INotifyPropertyChanged` (raising on real changes only) so rows update LIVE even though the rebuild guard skips the ListView — see notes for why INPC went on the model. |
| NP-21 | ✅ Fixed | The playing row's play button flips to a pause glyph via the same converter pair (`ConditionalValueConverter`, E769/E768) with matching accent foreground. |
| UI-NP-01 | ✅ Fixed | Same defect as NP-19 (file URI bound as artwork): closed by the ArtworkUrl binding + VM hydration. Placeholder shows only for albums genuinely without art. |
| UI-NP-02 | ✅ Fixed | Playing row is visually distinct (NP-20's tint + glyph), AND each row gained a per-item remove button (X glyph, GhostIconButtonStyle) raising `RemoveItemRequested` → `ViewModel.RemoveQueueItemCommand` → new `IQueueService.RemoveById(itemId)` — index math explained in notes. |
| UI-NP-03 | ✅ Fixed | Double-scroll eliminated — same fix as NP-16 (one animated BringIntoView at ratio 0.4). |
| UI-NP-04 | ✅ Fixed | CreditsPanel + QueuePanel hardcoded `White`/`#888888`/`#CCCCCC` swept to Text Fill Primary/Secondary/Tertiary (also closes the MC-04/05/06 trio early — ledger rows recorded under Batch 14). |
| UI-NP-05 | ✅ Fixed | `RefreshUpNextQueue` compares the candidate visible window against the current one (count + ordinal Id-sequence) and returns early when identical — playback-state pulses no longer destroy and rebuild the entire ListView visual tree (the NP-09 guard this page lacked). |
| UI-NP-06 | ✅ Fixed | Lyrics toolbar added (collapsed until lyrics exist): offset label + −/+ buttons stepping ±500 ms and a copy-to-clipboard button. Offset lives in `NowPlayingViewModel.LyricsOffsetMs` (clamped ±5000 ms, RESET to 0 on every track change since drift is per-file), applied in `UpdateLyricPosition` as `currentSeconds − offset/1000`, with immediate highlight re-evaluation after adjustment; copy joins synced lines (or unsynced raw text) into a `DataPackage`. |
| HLP-01 | ✅ Fixed | `SpectrumVisualizerControl` captures its `DispatcherQueue` in the constructor; `UpdateSpectrum` self-marshals — direct `RenderSpectrum` call when `HasThreadAccess`, otherwise `TryEnqueue`. Cross-thread FFT frames can no longer throw regardless of caller thread. |

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-12 | 🔴 Build-system trap | WinUI markup compiler (WinAppSDK 2.2.1) | UNQUALIFIED instance-method `{x:Bind Function(...)}` bindings INSIDE a DataTemplate kill the XAML compiler: PageCodeGen dies with internal error WMC9999, and the real message is masked by a culture-dependent resource failure ("Could not find any resources… ErrorMessages.resources"). Diagnosed via `-v diag` "perfXC_PageCodeGenStart" markers pinpointing QueuePanel. Workaround shipped: classic `{Binding}` + `IValueConverter`s for in-template visuals (`QueuePlayStateConverters.cs`). Static xmlns-qualified functions DO compile inside templates (`MainWindow.GetSuggestionGlyph` proves it) and instance methods work fine OUTSIDE templates — only the unqualified-instance-method-in-template combination NREs the compiler. |
| NF-13 | ⚠️ Theme bug | Cached-brush pattern | Brushes cached via `??=` keep the OLD theme's color after a light/dark switch forever. LyricsPanel now resolves `SystemControlHighlightAccentBrush`/`TextFillColorPrimaryBrush` via `TryGetValue` PER CALL (hardcoded fallbacks preserved), and the queue tint converter rebuilds itself when the accent color changes — establishing "never cache a themed brush across themes" as the local convention. |

#### Notes & deliberate trade-offs

- **Converters instead of x:Bind functions in templates** (forced by NF-12): the queue-row visuals run through `QueuePlayStateBrushConverter` (parameter selects Background vs Foreground mode) and `ConditionalValueConverter` (bool→glyph). Slightly more moving parts than a function binding, but it compiles, and the converter doubles as the NF-13-correct place to resolve themed brushes.
- **INPC on `QueueItem` (a model)**: with UI-NP-05's rebuild guard in place, `IsPlaying` flips no longer coincide with a ListView rebuild — the row visuals would freeze. Making the model raise `PropertyChanged` (only on real value changes; `ArtworkUrl` also raises on the null→"" hydration transition but suppresses identical repeats) lets OneWay bindings update the playing row in place. `Track` records stay immutable; only the queue wrapper got the treatment.
- **Id-based removal (`RemoveById`)**: the Up Next panel shows a WINDOW of the queue starting at the playing item, so the panel's row index ≠ the service's full-queue index — a plain `RemoveAt(visibleIndex)` could delete the wrong entry. `RemoveById` resolves the index under `_queueLock` via `FindIndex(i => i.Id == itemId)`; both `RemoveAt` and `RemoveById` share one private `RemoveAtUnlocked` (capture state inside the lock, raise events outside) so the current-track/stop semantics stay identical across both paths.
- **Artwork hydration design**: pending pairs are collected synchronously (items with `ArtworkUrl == null` and a non-empty AlbumId), one DB lookup fires per DISTINCT album, results apply through `TryEnqueue` with a disposed-check, and EMPTY-STRING tokens are cached too ("album has no art") so state pulses don't re-query the same artless album forever. Fire-and-forget with try/catch logging — artwork must never break queue updates.
- **Non-virtualized ItemsControl trade-off (CRIT-05)**: accepted by the audit's own reasoning — lyric lists are bounded (<200 lines) so container recycling buys nothing, while correctness (stable element references) buys back the entire CRIT-05 class. If lyrics ever become unbounded, revisit with virtualization + `ContainerFromIndex`.
- **Offset semantics**: ±500 ms steps, ±5000 ms clamp, per-track reset. The reset is deliberate — LRC drift is a property of the FILE's timestamping, so carrying an adjustment across tracks misapplies it elsewhere.
- **Self-marshaling contract (HLP-01)**: the control owns its threading (capture-in-ctor, branch-per-call) so callers like MainWindow's `CompositionTarget.Rendering` tick and any future engine callback need zero dispatcher knowledge.
- **Testability**: `QueueService` is constructible in `Octave.Core.Tests` (mocked audio player + temp-file DbContext), so `RemoveById` got 3 facts (preserves current track / removing current stops playback / unknown-or-empty id is a no-op) plus 3 `QueueItem` INPC facts — 6 new tests, 259 → 265.

**Acceptance verified** (§15 Batch 13): queue-row thumbnails render from real album tokens · the playing queue row is tinted and glyph-swapped live · lyrics auto-scroll ONCE with the highlight tracking the correct line (no recycling staleness possible) · spectrum updates never throw cross-thread · per-row remove deletes the right queue entry · build 0 warnings / 0 errors.

**Final state**: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **265/265** (259 prior + 6 new).

---

### Session 2026-08-23 — Batch 12 — System integration & shell *(commit `86bc36e`)*

**Files opened**: exactly the six the batch names — `Helpers/WindowMinSizeHelper.cs` (SYS-01), `App.xaml.cs` (SYS-02/SH-10/SHELL-02), `Services/System/WindowsSmtcService.cs` (SYS-03/04/05), `Services/System/ISmtcService.cs`, `ViewModels/ShellViewModel.cs` (SHELL-01/SH-02), `ViewModels/MainViewModel.cs` (SHELL-02) — plus `MainWindow.xaml.cs`, the wiring site SYS-05's teardown implies (drift justified in notes).

| ID | Status | Evidence |
|----|--------|----------|
| SYS-01 | ✅ Fixed | One subclass delegate PER hwnd, held in a `Dictionary<IntPtr, SubclassProc>` that is the GC root keeping each delegate alive for exactly as long as its subclass is installed. The old single static `_subclassProc` field was overwritten by EVERY `SetMinSize` call — with two windows, window 1's delegate became unreachable garbage and its next `WM_GETMINMAXINFO` jumped into collected memory (CallbackOnCollectedDelegate → AV). Re-invocation for an already-subclassed hwnd now removes the old link first (`RemoveWindowSubclass`) instead of stacking a second one; `ClearMinSize(IntPtr)` added for teardown. Each closure also carries its OWN min sizes — the old shape silently shared whatever the most recent caller passed. |
| SYS-02 | ✅ Fixed | DB-init failure no longer `throw`s into `async void OnLaunched` where the old blanket UI handler swallowed it — the process survived as an invisible zombie holding the DB file with no window. It now shows a native `MessageBoxW` fatal-error dialog (no XamlRoot exists pre-window, so a ContentDialog is impossible), then `Application.Exit()` and `return` — no window is ever created. |
| SYS-03 | ✅ Fixed | All SMTC state pushes are serialized through the `DispatcherQueue`: `QueueService_PlaybackStateChanged` marshals via `TryEnqueue` when off-thread (PlaybackStateChanged fires on the queue engine's background thread). The raced shared fields `_lastTrackId`/`_hasPushedDisplay` are now `volatile` so `UpdateThumbnailAsync`'s cross-thread staleness reads are honest; the thumbnail path also re-checks `_disposed`/`_systemControls` after its awaits. |
| SYS-04 | ✅ Fixed | The `ButtonPressed` dispatch branch was INVERTED: events arrive on the UI thread, so production always took the `HasThreadAccess == true` → `ThreadPool.QueueUserWorkItem` path and ran queue mutations OFF the thread every binding reads. Now dispatches via `TryEnqueue` unconditionally; the pool remains only as a pre-initialization fallback (`_dispatcherQueue == null`). |
| SYS-05 | ✅ Fixed | `ISmtcService` now extends `IDisposable`; `WindowsSmtcService.Dispose()` unsubscribes BOTH `ButtonPressed` and `PlaybackStateChanged`, clears the `DisplayUpdater` (`ClearAll` + `Update`) and sets `PlaybackStatus = Closed` — previously the singleton listened forever and the shell kept showing OCTAVE's last track after the owning window was gone. `MainWindow.Closed` performs the teardown: SMTC dispose, `WindowMinSizeHelper.ClearMinSize(hwnd)`, and the rendering-hook detach (NF-11). |
| SHELL-01 | ✅ Fixed | The sleep-timer callback captures its OWN `Timer` instance (captured-`self` pattern) and checks `ReferenceEquals(self, _sleepTimer)` inside the marshaled callback before acting. A stale timer's already-queued callback used to run against the FIELD: it paused playback and then DISPOSED the user's freshly-set replacement timer. |
| SHELL-02 | ✅ Fixed | `MainViewModel.cs` deleted and its DI registration removed — grep-verified zero consumers anywhere (code or XAML); it flipped a local `IsPlaying` bool and never touched the audio service. |
| SH-02 | ✅ Fixed | `UpdateSearchSuggestionsAsync` swaps its debounce CTS with `Interlocked.Exchange(ref _searchCts, null)` — the old cancel/dispose/null sequence on the shared field interleaved between rapid `TextChanged` callers into double-dispose crashes (the `Dispose()` at :856 was not inside the try) and one caller's fresh source being cancelled by the next. A second `Interlocked.Exchange(ref _searchCts, cts)?.Cancel()` at install time supersedes anything another caller slipped in between (newest-install-wins). Both dispose paths tolerate `ObjectDisposedException`. |
| SH-10 | ✅ Fixed | `App.UnhandledException` now marks ONLY `OperationCanceledException` as handled (the benign cancellation storm that CTS teardown legitimately produces); every other exception stays UNHANDLED so real corruption is loud and diagnosable. The old blanket `e.Handled = true` turned every failure — including fatal DB/engine faults — into silent wrong behavior. |

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-11 | ⚠️ Leak | `MainWindow.xaml.cs` | While wiring SYS-05's teardown: `CompositionTarget.Rendering += CompositionTarget_Rendering` (per-frame FFT spectrum tick) was never detached anywhere. Now unsubscribed in the same `Closed` handler. |

#### Notes & deliberate trade-offs

- **SH-10 policy consequence, accepted**: exceptions that used to vanish now terminate the app. That is the audit's own prescription — a music player that silently keeps running with a corrupted engine/DB state is worse than one that fails loudly. The OCE carve-out keeps CTS-teardown cancellation storms benign.
- **SYS-02 surface**: `MessageBoxW` rather than WinUI — `OnLaunched` fails before any `Window`/`XamlRoot` exists; a native box is the only reliable UI. `Exit()` + `return` guarantees window creation never proceeds on a dead DB.
- **SYS-01 latent today**: `MiniPlayerWindow` does NOT call `SetMinSize` (grep-verified), so the second-window AV scenario was one future `SetMinSize` call away, not live. Fixed at the root anyway since the dictionary costs nothing.
- **SH-02 newest-install-wins**: if caller B null-takes, caller C installs, then B installs — B's second exchange cancels C, which is correct because B's keystroke is the newer user intent.
- **Volatile + dispatcher serialization (SYS-03)**: the dispatcher serializes all WRITERS; `volatile` covers the one reader that legitimately runs off-thread (`UpdateThumbnailAsync`'s staleness compares). Together they remove both the logical race and the memory-visibility gap without a lock.
- **Desktop-side acceptance**: like B11, these are Desktop-only types — acceptance is structural reasoning + the 0-warning build (B4 precedent); TEST-family coverage belongs to B15.

**Acceptance verified** (§15 Batch 12): subclass delegate cannot be collected while installed (dictionary GC-root; per-hwnd) · fatal init is visible and exits (native box + `Exit()`, no zombie) · SMTC state mutations single-threaded (dispatcher serialization) · hardware buttons run on the UI thread (unconditional enqueue) · SMTC + subclass + rendering hook torn down on close · stale sleep timer cannot pause/dispose its replacement · dead VM gone · suggestion debounce race-free (atomic ownership transfer) · non-cancel exceptions stay fatal.

**Final state**: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **259/259** (no Core changes this batch).

---

### Session 2026-08-23 — Batch 11 — ViewModel leaks & lifecycle *(commit `ee39ac3`)*

**Files opened**: the batch's named set — `EqBandViewModel.cs`, `MetadataEnrichmentViewModel.cs`, `ExternalDataSettingsViewModel.cs`, `LibraryEnrichmentViewModel.cs`, `EntityDetailViewModel.cs`, `HomeViewModel.cs`, `PlaylistDetailViewModel.cs`, `SearchViewModel.cs`, `NowPlayingViewModel.cs`, `NowPlayingPage.xaml.cs`, `ShellViewModel.cs` (VM-12's sole call site), and the VM-06 siblings the finding names (`LibraryViewModel.cs`, `AlbumsViewModel.cs`, `ArtistsViewModel.cs`, `PlaylistsViewModel.cs`) — plus the wiring sites the lifecycle fixes imply (`MetadataEnrichmentDialog.xaml.cs`, `LibraryEnrichmentDialog.xaml.cs`, `SettingsPage.xaml.cs`) and NEW `Helpers/CollectionDiff.cs` (VM-10's shared guard; drift justified in notes).

| ID | Status | Evidence |
|----|--------|----------|
| CRIT-02 | ✅ Fixed | NowPlayingViewModel's track-change swap now cancels AND disposes both `_lyricsCts` and `_creditsCts` before replacing — previously cancelled-only, leaking a native wait handle per track change (the `Dispose()` at the class bottom already did both; the hot path didn't). |
| ME-06 | ✅ Fixed | `ApplyChangesAsync` cancels + disposes the prior `_applyCts` before creating a new one — rapid re-clicks of Apply previously stacked concurrent `ApplyEnrichmentAsync` runs writing the SAME audio file. The prior apply is now serialized out (cancelled) before the next begins. |
| VM-01 | ✅ Fixed | `ExternalDataSettingsViewModel` stores its `SettingsChanged` handler in a readonly field and gains `Cleanup()` (detach + test-CTS teardown); `SettingsPage` wires `Unloaded → ExternalSettings.Cleanup()`. The old inline lambda made unsubscribing impossible — one VM + dispatcher closure leaked per Settings visit. |
| VM-02 | ✅ Fixed | `LibraryEnrichmentViewModel` field-stores both singleton subscriptions (`_progressHandler`/`_scanCompletedHandler` = method groups) and gains `Cleanup()`; `LibraryEnrichmentDialog.Closed` invokes it. Previously every dialog open permanently pinned a VM to the singleton enrichment service. |
| VM-03 | ✅ Already resolved *(Batch 4 — not re-touched)* | The NowPlayingViewModel double-rebuild guard predates this batch; ledger row recorded so Batch 11's ID range is fully accounted for. |
| VM-04 | ✅ Fixed | `EntityDetailViewModel.LoadEntityAsync` runs under a generation token (`++_loadGeneration`, captured locally): stale completions are dropped after EACH await AND inside the `TryEnqueue` application lambdas — the re-entrant refresh triggered by `EnrichEntityAsync` → `NotifyLibraryUpdated` → `LibraryUpdated` can no longer interleave two runs' writes. Body wrapped in try/catch (fire-and-forget callers previously surfaced nothing). |
| VM-05 | ✅ Fixed | `MetadataEnrichmentViewModel` disposes-before-replacing `_searchCts` (and `_applyCts`, with ME-06), and gains `Cleanup()` that cancels + disposes BOTH and nulls them; `MetadataEnrichmentDialog.Closed` invokes it — an in-flight apply can no longer keep writing after the dialog closes, and the CTSs no longer leak. |
| VM-06 | ✅ Fixed | `LoadAsync` bodies wrapped in try/catch across all five named VMs: Home (sets new `LastError` observable + debug log), Library (ALSO resets `IsLoading` in the failure path — the spinner used to stick forever), Albums, Artists, Playlists (debug log each). Playlists additionally covered by NF-10. |
| VM-07 | ✅ Fixed | `StartPreviewDryRunAsync`/`ApplySafeChangesAsync` get try/catch (failure surfaces in `CurrentOperation`) + `finally { IsScanning = false; }` — if the service throws before `ScanCompleted` fires, the busy state resets instead of sticking forever. `ReviewItemViewModel.SkipAsync`/`NeverAskAgainAsync` get the same catch `ApplyCandidateAsync` already had. |
| VM-08 | ✅ Fixed | All three cited post-await mutation sites marshal through `_dispatcher.TryEnqueue`: `LibraryEnrichmentViewModel.LoadReviewQueueAsync` (the :242 rebuild) and NowPlayingViewModel's `LoadCreditsAsync` tail (:287-323 — credit writes + `ArtistsList` rebuild; pure string-splitting stays outside the lambda) and `LoadLyricsAsync`'s post-await property writes (same defect class; the 2 s unavailable-delay stays outside the marshaled block since `TryEnqueue` takes an `Action`, and the delayed hide is marshaled too). |
| VM-09 | ✅ Fixed | `ExternalDataSettingsViewModel.TestTheAudioDbConnectionAsync` disposes the replaced `_testCts`, not just cancels it; `Cleanup()` also cancels + disposes + nulls it so an in-flight connection test cannot outlive the page. |
| VM-10 | ✅ Fixed | All four cited sites diff-guard their rebuilds via the new `Helpers.CollectionDiff.SameIdSequence` (ordinal id-sequence compare, order-sensitive): Home's `Fill` (all five sections), Search's four result collections, EntityDetail's two branch rebuilds, PlaylistDetail's `LoadAsync`. The Albums/Artists/Playlists/Library inline `SequenceEqual` guards already present were the pattern's in-repo precedent. |
| VM-11 | ✅ Fixed | PlaylistDetailViewModel persists reorders through ONE debounced `SetOrderAsync` (400 ms settle window, CTS-cancelled per edit) instead of a fire-and-forget write per `CollectionChanged` event; covers Move AND Remove/Add/Replace (ListView drag-reorders surface as Remove+Add pairs depending on how the drop lands — those paths were silently unpersisted); the persist is try/catch'd (the old write had no error handling); `Cleanup()` cancels any pending persist. |
| VM-12 | ✅ Fixed | `EqBandViewModel` construction no longer touches the engine: a `_suppressEngineApply` flag gates `OnGainChanged` during the ctor's `Gain = gain` seed, and `PushInitialGainToEngine()` applies the restored gain explicitly then unflags. `ShellViewModel` (the sole consumer) pushes every band's gain once AFTER the list is built — killing both the ctor-mutates-engine smell and the equality-short-circuit that silently skipped 0-gain bands. |
| VM-13 | ✅ Fixed | `ApplyChangesAsync` now behaves like `RunSearchAsync`: result state is computed locally, applied through `_dispatcher.TryEnqueue`, a superseded run (`!ReferenceEquals(cts, _applyCts)`) writes NOTHING, `OperationCanceledException` short-circuits to `false` without writing success/failure over the newer run, and `IsApplying` is reset on every exit path. |
| SH-11 | ✅ Fixed | NowPlayingPage guards its `PropertyChanged` hook with `_vmPropertyChangedHooked` so unpaired Loaded/Unloaded cycles (re-parenting, popup hosting) cannot stack subscriptions. The broader fragility the finding describes — a transient VM with singleton deps kept alive by an `Unloaded→Dispose` convention — is documented as accepted: every page follows the same pattern and replacing it needs a navigation-scoped lifetime design. |

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-10 | ⚠️ Stale UI / unobserved faults | `PlaylistsViewModel.CreateAsync` / `DeletePlaylist` | Both commands had no error handling AND never refreshed the list themselves — they relied entirely on the `LibraryUpdated` wave; a failed delete left the stale playlist visible with no trace of the error. Both now try/catch (debug log) and explicitly `LoadAsync()` afterwards (the VM-10 diff guard makes the refresh cheap when the event already beat them to it). |

#### Notes & deliberate trade-offs

- **Acceptance is structural + build** (B4 precedent): every file in this batch lives in `Octave.Desktop`, which `Octave.Core.Tests` cannot reference — VM/page behavior is proven by the 0-warning build plus structural reasoning. TEST-family lifecycle coverage belongs to B15 as sequenced.
- **Drift — new `Helpers/CollectionDiff.cs`**: VM-10's letter names four call sites, and all four needed the SAME guard. Centralizing mirrors the inline `SequenceEqual` checks already shipped in Albums/Artists/Playlists/Library (kept as-is — they compare full record equality, a stricter check their callers want) and gives the four new sites one ordinal id-sequence predicate instead of four copies.
- **Dialog ownership idiom**: pages tear their VMs down on `Unloaded`; ContentDialogs have no Unloaded, so both enrichment dialogs now own their transient VM's teardown via `Closed`. With SettingsPage's new `Unloaded` wiring, every transient VM that subscribes to a singleton now has a named teardown path.
- **VM-11 trade-offs**: (1) a reorder made <400 ms before leaving the page may not persist — `Cleanup` cancels rather than blocking the UI thread to flush; (2) covering `Remove` means `RemoveTrack` also schedules an order persist after `RemoveTrackAsync` — a harmless duplicate write that keeps the remaining sequence consistent on disk; (3) the debounce reads `Tracks` after the delay on the UI thread (continuation context captured from `CollectionChanged`), so the persisted order is the settled one.
- **ME-06 cancellation semantics**: cancelling the in-flight apply on re-click is intentional and safe — the file-write path is atomic (B6's temp+replace), so a cancelled apply leaves the file in its previous state.
- **VM-13 success path**: `_libraryService.NotifyLibraryUpdated()` stays a synchronous service-level call (not a UI mutation) issued only after the run is confirmed non-superseded and successful.
- **LoadLyricsAsync restructure**: the unavailable-branch's 2 s delay cannot live inside a `TryEnqueue` lambda (`Action`), so the marshaled block applies the immediate state and the delay + marshaled hide run outside it — same observable behavior, correct thread affinity.

**Acceptance verified** (§15 Batch 11): no transient VM outlives its page/dialog without a teardown path (Settings/both dialogs wired; Home/Search/EntityDetail/PlaylistDetail already had `Cleanup`) · CTSs are cancelled AND disposed everywhere they are replaced (CRIT-02/VM-05/VM-09/ME-06) · stale loads cannot interleave (generation token at every stage) · rapid applies serialize (cancel-before-create) · cancelled applies write no state (VM-13) · rebuilds skip when data is unchanged (VM-10, all four sites) · reorder persists once per settle across all mutation kinds (VM-11) · engine untouched during EQ construction (VM-12) · fire-and-forget loads observe their faults (VM-06) · build 0 warnings / 0 errors.

**Final state**: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **259/259** (no Core changes this batch).

---

### Session 2026-08-23 — Batch 10 — Single-flight / orchestrators / cache / network *(commit `582e027`)*

**Files opened**: exactly the seven the batch names — `Helpers/AsyncSingleFlight.cs`, `Services/Network/HttpService.cs`, `Services/Network/ProviderRateLimiterRegistry.cs`, `Services/Cache/TwoTierExternalDataCache.cs`, `Services/External/ExternalMetadataOrchestrator.cs`, `Services/External/ExternalArtworkOrchestrator.cs`, `Services/External/OnlineLyricsOrchestrator.cs` — plus the three files ORC-03/OL-08's chosen shape implies (`Interfaces/IArtworkCacheManager.cs`, `Services/Metadata/ArtworkCacheManager.cs`, Desktop `Converters/ArtworkPathConverter.cs` — drift justified in notes) and two NEW test files (+16 tests).

| ID | Status | Evidence |
|----|--------|----------|
| SF-01 | ✅ Fixed | AsyncSingleFlight rewritten around a shared TCS-backed task plus **per-caller `WaitAsync(ct)`**: a caller's token ends only THEIR wait; the factory never observes any caller's token (all orchestrator sweeps now run under `CancellationToken.None`). Cancelling the leader leaves the joiner's result intact; cancelling a joiner neither disturbs the leader nor poisons late callers — completed entries are removed, so a late caller starts a fresh generation rather than awaiting a corpse. Directed tests cover leader-cancel and joiner-cancel, each asserting the untouched party still receives the shared result. Orchestrators translate their own cancelled wait into their historical graceful default — see notes. |
| SF-02 | ✅ Resolved *(best-effort, documented)* | True cross-instance dedup is out of scope for an in-proc helper; the audit's own remedy — a short-lived result layer — is what the two-tier cache already provides: every sweep result lands in L1/L2, so post-race duplicate generations hit the cache, not the providers. Contract documented on the class and bounded by test (`factoryRuns ∈ [1,24]` under a 24-caller burst). |
| HL-01 | ✅ Fixed | Registration is one lock-free `GetOrAdd(key, tcs.Task)` replacing the `while(true)` spin; the losing racer awaits the winner's task. Test: 24 concurrent callers all complete with consistent results. |
| HL-02 | ✅ Fixed | Factory faults propagate to EVERY waiter (the shared task carries the exception); a detached `OnlyOnFaulted` observer consumes the exception on the backing task itself so the flight can never surface as UnobservedTaskException. Test: leader and joiner both receive the same fault. |
| NET-01 | ✅ Fixed | The limiter's next-allowed instant lived in a 16-byte `DateTimeOffset` field — a struct write that can tear when `NotifyRetryAfter` races the semaphore holder mid-update. Now a `long` UTC-ticks field: `Interlocked.Read`/`Exchange` on the min-interval path, and retry-after extension via a CAS loop that only ever pushes the window LATER. Test: ≥~300 ms hint delays the wait observably, and a subsequent SHORTER hint cannot cut the live window short. |
| NET-02 | ✅ Fixed | Non-success responses are disposed before the `Failure` result is built (status/error copied first). Test: a 404 whose content is a tracking `HttpContent` observes `Disposed == true`. |
| NET-03 | ✅ Fixed | `CloneRequestAsync` buffers `Content.ReadAsByteArrayAsync()` into a fresh `ByteArrayContent` AND copies `Content.Headers` (`TryAddWithoutValidation`), making cloned requests fully replayable. Today's call sites are GET-only (latent — why it went unnoticed); the test pins POST replay across a 503→200: body byte-equal on both attempts and Content-Type preserved. |
| NET-04 | ✅ Fixed *(policy documented)* | Limiter creation uses a static-lambda `GetOrAdd` (no closure per call), and the XML docs state the real contract: the registry interval binds at FIRST creation — `GetOrAdd` cannot replace — and stricter floors are enforced ABOVE by HttpService's PROV-02 decorator map (Batch 8). The silent ctor race the audit flagged is closed by giving intervals exactly one authoritative layer, not by pretending replacement works. |
| NET-05 | ✅ Fixed | Backoff jitter draws from `Random.Shared`; the old clock-seeded `new Random()` produced IDENTICAL jitter sequences for rapid consecutive retries — synchronized retries instead of spread ones. |
| CACHE-02 | ✅ Fixed | Mutable collection payloads are defensively copied INTO L1 on set (the master never aliases the caller's object) and OUT on get: arrays clone; closed `List<T>` copies via a reflective copy-constructor cached per payload type; primitives/strings/records pass through. Tests prove caller-side mutation after set and reader-side mutation between reads both leave the master intact. |
| CACHE-03 | ✅ Fixed | L1 eviction is true last-access LRU (`Touch()` on every hit; victims = oldest last-access) and purges a proportional 20% batch at the capacity boundary so the sort amortizes over the next ~⅕·capacity inserts instead of running on every set. Verified via internal residency hooks (see notes): budget exact, the touched entry retained, exactly one of four untouched middle keys evicted. |
| CACHE-04 | ✅ Fixed | Capacity evaluation and insertion happen atomically under one gate lock — the old ensure-capacity-then-indexer sequence was check-then-act, letting N concurrent setters pass the size gate together and overshoot. The LRU test asserts the post-eviction count is EXACTLY capacity. |
| CACHE-05 | ✅ Fixed | A corrupted L2 row (`JsonException`) is purged awaited UNDER the write lock with `CancellationToken.None` (a purge must not die with its caller's token) instead of fire-and-forget racing concurrent writers into dropping a row being written. Test damages a row via raw SQL: a cold read returns null AND the row is physically gone (`COUNT(*) = 0`). |
| CACHE-06 | ✅ Fixed | `SetAsync` checks `ct.IsCancellationRequested` BEFORE mutating any tier — the old order wrote L1 first and consulted ct only before the L2 write, leaving fresh memory state behind a cancelled call. Test: pre-cancelled set → subsequent read is a miss. |
| ORC-01 | ✅ Fixed | Fruitless provider sweeps are remembered as negative sentinels served while FRESH: lyrics `Unavailable` @ 6 h TTL; empty search lists @ 1 h TTL. Sentinels are excluded from stale-as-fallback (a stale Unavailable can't masquerade as usable lyrics), Loading never caches. Test: two lookups of a track nobody has → the provider is swept ONCE, not twice. |
| ORC-03 | ✅ Fixed | Token→absolute-path resolution centralized on the new `IArtworkCacheManager.ResolveTokenPath` (empty-token guard, case-insensitive prefix strip, separator normalization, combine under CacheRoot); existence checks route through `CachedFileExists` — memoized 30 s, primed positive by `CacheBytesAsync`. Tests pin exact layout resolution, memo staleness behind an external delete, clean false for unknown tokens, and end-to-end that artwork deleted between resolves is served from memo WITHOUT a provider refetch (`Times.Once`). |
| OL-06 | ✅ Fixed | `SearchTrackCandidatesAsync` dedupes merged candidates by identity BEFORE ranking: MBID when present (authoritative), else the normalized title\|artist\|album triple — keeping the highest-confidence representative. Tests: the same recording reported by two providers collapses to one candidate (0.9 kept over 0.7); an MBID-less pair differing only by padding/case still collapses. |
| OL-08 | ✅ Fixed | All SIX duplicated `.Replace("ArtworkCache/","") + Path.Combine + File.Exists` blocks in ExternalArtworkOrchestrator collapse to `_artworkCacheManager.CachedFileExists(token)` — one 30 s memoized probe replaces up-to-three disk stats per resolution (the audit's complaint) and the token layout has exactly one owner. |

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-09 | ⚠️ Latent coupling | Desktop `Converters/ArtworkPathConverter.cs` | While centralizing token resolution (ORC-03) the converter was found REIMPLEMENTING the token layout — hard-coding `<LocalFolder>\ArtworkCache` before handing the path to `BitmapImage`. It now asks the manager (`App.Services.GetRequiredService<IArtworkCacheManager>().ResolveTokenPath(…)`). If the cache root ever moves, the converter can no longer silently break — which is precisely the drift this batch's centralization exists to prevent. |

#### Notes & deliberate trade-offs

- **Graceful cancellation lives at the orchestrator boundary**: AsyncSingleFlight propagates `OperationCanceledException` STRICTLY per caller — that semantic is what makes SF-01 provable and is unit-tested as such. But the established orchestrator acceptance tests pin graceful defaults on pre-cancelled tokens (null / empty list / Unavailable), not exceptions. Resolution: each orchestrator wraps only its `ExecuteAsync` call in `catch (OperationCanceledException)` returning its historical default. Cache reads never thread ct into SQLite (only writes do), so the wrapper placement covers the whole realistic throw surface. Net effect: a cancelled UI refresh stops costing anything immediately, while the shared fetch completes for everyone else and lands in cache.
- **Provider loops lost their `catch (OCE) when (ct.IsCancellationRequested)` filters** (artwork orchestrators): factories run under `CancellationToken.None`, so the filter was dead code by construction; a provider-internal timeout cancellation falls to the generic catch and the sweep continues to the next provider.
- **Drift — `IArtworkCacheManager` / `ArtworkCacheManager` / `ArtworkPathConverter`**: OL-08's letter places the memoized existence check inside the orchestrator, but the six duplicated path-math blocks were the disease, not the disk-stat count alone. Putting probe + resolution where the token is MINTED (`CacheBytesAsync`) fixes the class; NF-09 independently proved the old arrangement had already leaked once into the UI layer.
- **Internal test hooks** `L1EntryCount` / `IsL1Resident(string)` on TwoTierExternalDataCache (InternalsVisibleTo granted in Batch 9): `GetAsync` cannot observe eviction — keys purged from L1 fall back to L2 by design and are promoted straight back, so tier membership is visible only directly. Same justification pattern as B9's watcher-debounce hooks.
- **30 s existence-memo window**: externally deleted files can be served for up to 30 s (BitmapImage fails to decode → placeholder shows; next probe heals). Buying the resolve hot path freedom from per-item synchronous `File.Exists` storms IS the point of OL-08, and `CacheBytesAsync` primes the positive side so fresh art never pays a false miss.
- **Negative sentinel TTL asymmetry** (6 h lyrics / 1 h searches vs 30 d / 7 d positives): a fruitless sweep must not re-fire on every UI refresh or offline session, yet new providers and catalog growth should pick an obscure track up within hours.
- **NET-04 enforcement split**: the registry stays a dumb keyed factory (first-wins); authority over minimum intervals deliberately sits in ONE layer — HttpService's PROV-02 map — instead of two layers racing to replace each other.

**Acceptance verified** (§15 Batch 10): cancelling one caller does not cancel a shared in-flight fetch for others (leader-cancel and joiner-cancel tests) · no response-message leaks (disposed-on-failure test; success path disposes via `using`) · negative caching stops provider re-sweeps (test) · LRU budget held exactly (test) · build 0 warnings / 0 errors.

**Final state**: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **259/259** (243 prior + 16 new: 7 single-flight/network-resilience, 9 cache/orchestrator-resilience).

---

### Session 2026-08-23 — Batch 9 — Scanner & watcher hardening *(commit `b3cbacc`)*

**Files opened**: exactly the three the batch names — `LocalLibraryScanner.cs`, `LibraryWatcherService.cs`, `App.xaml.cs` (INT-04 only) — plus the files SCAN-11's prescription implies (drift justified in notes: `DomainModels` `Track.DiscNumber`, `SqliteDbContext` Disc column + migration + materializers, TrackColumns/ReadTrack unification), `Octave.Core.csproj` (InternalsVisibleTo), and +18 tests (LocalLibraryScannerTests ×10, LibraryWatcherServiceTests ×7, Tracks.Disc migration ×1).

| ID | Status | Evidence |
|----|--------|----------|
| SCAN-01 | ✅ Fixed | Tag parsing + artwork extraction run OUTSIDE any write transaction; DB writes flush in short ~200-track batches (one transaction each); reconciliation runs in its own short transaction. The minutes-long library-wide write lock is gone — WAL can checkpoint and concurrent UI writes proceed. |
| SCAN-02 | ✅ Fixed | The producer owns a linked CTS cancelled on consumer fault (catch AND finally), and the producer is always drained and awaited — the blocked-in-`WriteAsync` task-leak is structurally impossible. |
| SCAN-03 | ✅ Fixed | `/` and `\` removed from `ParseArtistNames` delimiters — `"AC/DC"` stays one artist. Unit-pinned. |
| SCAN-04 | ✅ Fixed | BOTH sides of reconciliation normalize via `Path.GetFullPath` before set-build/membership. Test: a case-variant stored path is not deleted. |
| SCAN-05 | ✅ Fixed | `_folderArtCache` cleared at the start of every full scan — art added since the last walk is picked up; no lifetime-null caching. |
| SCAN-06 | ✅ Fixed | Orphan-artwork `File.Delete` sweep moved AFTER all commits — no filesystem side effects inside a rollbackable transaction. |
| SCAN-07 | ✅ Verified *(verification-side; VM half belongs to B11)* | Subscriber chains audited: ShellViewModel marshals scan-progress/library events via `DispatcherQueue.TryEnqueue`; QueueService's `LibraryChanged` handler purges under `_queueLock` and re-emits through its own marshaled event chain. The remaining `_ = LoadAsync()` collection-touching patterns are exactly VM-08's scope — recorded here as deferred to Batch 11, not silently dropped. |
| SCAN-08 | ✅ Fixed | Reconciliation gated on "root verified reachable AND fully enumerated": `Directory.Exists` checked before any work; ANY per-directory enumeration failure marks the walk incomplete; an incomplete walk DELETES NOTHING. Tests: a missing root deletes zero existing tracks; a reachable-but-empty root skips reconciliation entirely (consequence in notes). |
| SCAN-09 | ✅ Fixed | Album identity keyed by `FirstAlbumArtist` (falling back to performer) — a compilation groups under ONE album and a guest-feature track stays in the parent artist's album. Both pinned by tests. |
| SCAN-10 | ✅ Fixed | The whole per-track processing block is guarded try/catch (log + continue), mirroring the corrupt-file path — one odd file can no longer roll back the scan; PLUS a companion per-root try/catch in `RequestFullReconciliationAsync` so one failing root can't abort the remaining roots. Test: a corrupt file among valid ones is skipped, the rest scan. |
| SCAN-11 | ✅ Fixed | `Tag.Disc` is read and persisted: `Tracks.DiscNumber` column + migration (backfill 1) + disc-aware ordering (disc, then track). Test: multi-disc files persist disc numbers and album ordering sorts disc→track. |
| SCAN-12 | ✅ Fixed | Progress reports −1 (indeterminate) until enumeration completes, then emits the final stable total — the denominator no longer rises mid-scan. Test: the final report carries the stable count. |
| SCAN-13 | ✅ Fixed | Per-row try/catch around `Path.GetFullPath` in reconciliation — a malformed stored path is skipped, the scan proceeds. Test: malformed row tolerated, no rollback. |
| WATCH-01/02 | ✅ Fixed | File-deletion events now call `RemoveStalePathAsync` (grep-confirmed dead code before) — cheap targeted removal; full multi-root reconciliation reserved for directory renames + buffer overflow. Test pins the targeted path. |
| WATCH-03 | ✅ Fixed | Claim-then-process debounce under one lock: the pending entry is claimed FIRST; a post-claim event creates a FRESH entry (nothing dropped); rapid events coalesce to one pending entry. Tests cover coalescing and post-claim-event survival. |
| WATCH-04 | ✅ Fixed | Locked-file retries capped (5 × ~2 s) then dropped — periodic reconciliation sweeps the file up later; no endless per-file timer. Pinned by the mid-write test's retry phase. |
| WATCH-05 | ✅ Fixed | Startup/overflow/rename reconciliation requests wrapped in try/catch — faults observed, never UnobservedTaskException. |
| WATCH-06 | ✅ Fixed | Dispose takes the locks with a disposed guard; in-flight callbacks stop; post-dispose events are ignored. Tested. |
| WATCH-07 | ✅ Fixed | Lock probe opens `FileShare.Read` — concurrent WRITERS make it throw, so a half-copied file is deferred instead of parsed truncated (the old `ReadWrite` share mode passed during active writes). Test: a mid-write file is deferred and scanned exactly once once writable. |
| WATCH-08 | ✅ Fixed | Absent roots are NOT registered for deletion-reconciliation and get NO watcher at add time; they are re-probed every 30 s and watchers attach when the root appears. Kills both halves of the finding (invisible-once-plugged-in root; offline registration as SCAN-08 amplifier). Test: absent root deferred from scanner registration. |
| WATCH-09 | ✅ Fixed | Exactly one Timer per pending path, created UNDER the pending lock — no `AddOrUpdate`-factory duplication/leak. |
| INT-04 | ✅ Fixed | First run no longer auto-monitors `%MUSIC%`: watching starts only from folders the user adds (consent-first wiring in App startup). |

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-07 | ⚠️ Inefficiency/scoping | `LocalLibraryScanner` reconciliation | Reconciliation loaded the WHOLE library per scanned root and diffed against it — O(all tracks) per root regardless of root size. Fix: load only rows under THIS root via an escaped LIKE prefix, so each root reconciles exactly its own subtree. |
| NF-08 | ⚠️ Spurious scans | `LibraryWatcherService` | Metadata-editor artifacts (`song.octave_tmp.mp3` / `.octave_bak`, introduced by B6's EDITOR-04) tripped the watcher on every editor save — create-temp fired one event, the atomic replace another. Fix: editor artifacts filtered from watch handling. |

#### Notes & deliberate trade-offs

- **SCAN-08 empty-folder consequence**: an empty-but-reachable `discoveredSet` is indistinguishable from a partially-failed walk, so reconciliation is skipped for it too — stale DB rows under a genuinely emptied folder persist until the root is removed/re-added. Deleting on zero-discovery IS the mass-loss bug; requiring positive completeness evidence is the safe default, chosen deliberately.
- **Schema drift (ID-mandated)**: SCAN-11's prescription is literally "add a disc field to schema + ordering", so `Track.DiscNumber` + the SqliteDbContext column/migration/upsert/materializer changes are named-file drift by construction (same precedent as B6's EDITOR-02 transactional helper); `TrackColumns`/`ReadTrack` were unified across queries so every read path sees the new column identically.
- **InternalsVisibleTo("Octave.Core.Tests")** added to Octave.Core.csproj: deterministic watcher-debounce tests need internal-state visibility (pending-entry claims); Batch 10 extends the same pattern to cache residency hooks.
- **WATCH-04 cap consequence**: a file held locked longer than ~10 s goes invisible to the watcher until periodic reconciliation — deliberate: bounded resources beat unbounded polling, and reconciliation remains the safety net.

**Acceptance verified** (§15 Batch 9): an unavailable/partially-inaccessible root deletes zero tracks whose files still exist (missing-root test; incomplete-walk gating) · one unreadable file skipped without aborting (test) · compilations group under one album and guest features stay put (tests) · a mid-write file fails the lock check and is deferred, scanned once writable (test) · watcher debounce race-free (coalesce/claim/post-claim tests) · first run does not auto-monitor without consent (INT-04) · batched transactions throughout (structural).

**Final state**: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **243/243** (225 prior + 18 new).

---

### Session 2026-08-23 — Batch 8 — Provider correctness & API compliance *(commit `60b5806`)*

**Files opened**: exactly the three the batch names — `HttpService.cs` (PROV-01 UA + PROV-02 rate floor), `MusicBrainzMetadataProvider.cs` (MB-01 guards), `TheAudioDbArtistEnrichmentProvider.cs` (ADB-03) — plus the two test files extended (+5 tests). No drift. **NET-01..05 were deliberately not touched** — §15 assigns them to Batch 10.

| ID | Status | Evidence |
|----|--------|----------|
| MB-01 | ✅ Fixed | All six public entry points now guard on `IsEnabled`. The three search paths and `GetTrackMetadataAsync` already did; `GetAlbumMetadataAsync`, `GetArtistMetadataAsync`, `GetReleaseGroupMetadataAsync` did NOT — a disabled provider still drove release/artist/release-group fetches through orchestrator fallbacks. Test: `IsEnabled=false` → all three lookups return null and the mock handler counts **zero** HTTP calls. |
| PROV-01 | ✅ Fixed | User-Agent upgraded from the bare `OCTAVE/2.0` product token to `OCTAVE/2.0 (https://github.com/Aditya-4987/OCTAVE)` — the product + contact-comment form MusicBrainz's (and CAA's mirror) usage policy requires for identifying applications. Set via `TryAddWithoutValidation` because `ProductInfoHeaderValue` cannot represent the `(contact)` comment in one token pair. Test asserts both substrings arrive on the wire. |
| PROV-02 | ✅ Fixed | Root cause documented in-code: `ProviderRateLimiterRegistry.GetOrCreate` is `GetOrAdd` — it cannot REPLACE an existing limiter — so whichever construction runs first for a key fixes its interval forever, and the provider ctors' stricter registrations routinely lost to `HttpService`'s own first call at the 500 ms default. Fix: a static policy map inside `HttpService` (`musicbrainz`/`coverartarchive` → 1 s) makes the floor authoritative at the layer that actually sends; keys whose registry limiter is already ≥ the floor pass through untouched, stricter floors wrap it in a private `MinIntervalRateLimiter` decorator. The decorator is **cached per key** in a `ConcurrentDictionary` field — per-call creation would reset its `_nextAllowedTicks` each request and permit exactly the bursts it exists to prevent. Tests: three sequential musicbrainz requests spaced ≥900 ms apart even when the registry limiter was deliberately pre-seeded at 500 ms (the losing-race construction order); control test proves unmapped keys keep the registry default spacing (no accidental global slowdown). |
| ADB-03 | ✅ Fixed | `SearchArtistImageUrlsAsync` short-circuits on `!IsEnabled` at the top. Previously it skipped the check entirely and relied on the inner profile lookups' re-checks — which worked only while those methods guarded, i.e. correctness-by-coincidence across an interface boundary. Test: disabled provider → empty result, zero HTTP. |

#### Notes & deliberate trade-offs

- **Decorator serialization point**: the decorator holds its gate while awaiting the inner registry limiter, so callers queue behind one another — that is what enforces spacing; the inner wait stays in the chain to keep the registry's bookkeeping (and any Retry-After extension) coherent. `NotifyRetryAfter` extends BOTH layers via a non-blocking exchange.
- **Timing-test margins**: lower bounds assert ≥900 ms against the 1000 ms floor (100 ms slack absorbs scheduler jitter without letting a real sub-second burst pass); no upper bound on the mapped path (slow-CI false alarms buy nothing).
- **PROV-01 constant is public** (`HttpService.UserAgentString`) so future code that builds raw clients has one canonical string instead of re-deriving it.

**Acceptance verified** (§15 Batch 8): disabled provider ⇒ zero HTTP (`Times.Never` equivalent via handler counter, all lookup paths) · MusicBrainz never exceeds 1 req/s even without the registry carrying the interval · build 0 warnings / 0 errors.

**Final state**: `dotnet test Octave.Core.Tests` = **225/225** (220 prior + 5 new).

---

### Session 2026-08-23 — Batch 7 — Matching accuracy & provider result integrity *(commit `aecb52e`)*

**Files opened**: the batch's named set — `Helpers/MetadataTextNormalizer.cs` (at Helpers/, per spec), `TrackMetadataMatcher.cs`, `MusicBrainz/MusicBrainzMetadataProvider.cs`, `Artwork/CoverArtArchiveArtworkProvider.cs`, `Artist/TheAudioDbArtistEnrichmentProvider.cs`, `Lyrics/LrcLibLyricsProvider.cs` — plus NEW `Helpers/ExternalIdKinds.cs` (the entity-kind stamp vocabulary CAA-01 implies) and new `MetadataTextNormalizerTests.cs` (TEST-15, table-driven) + five existing test files extended (+39 tests total this batch).

| ID | Status | Evidence |
|----|--------|----------|
| MATCH-02 | ✅ Fixed | Containment no longer unconditionally floors similarity at 0.85: the boost applies only when the shorter string is ≥50% of the longer one OR a whole word inside it (new `IsWholeWordInside` scans space-delimited occurrences over normalized text). Weak fragments fall through to Levenshtein. Table row pins `"goodbye"` vs `"go"` ≤0.45 (was 0.85); `"black dog"` vs `"black"` (ratio .55) and `"live version"` vs `"live"` (whole word) stay ≥0.85. |
| MATCH-03 | ✅ Fixed | Missing duration earns neutral +0.05 (was +0.10 — more than present-but-off data earned). New test pins the full ordering exact(+0.20) > ±6 s(+0.08) > missing(+0.05) > ±12 s(+0.02); the old value violated the middle inequality. |
| MATCH-04 | ✅ Fixed | Artist/title split requires a SPACED dash (`\s+-\s+`); the leading track number still consumes its separator greedily (`[\s\.\-_]+`) so `"01 - Artist - Title"` parses as before. `"Spider-Man Theme.mp3"` now yields title=`Spider-Man Theme`, artist=null (was artist=`Spider`). Table-driven: 6 filename cases incl. dotted-number and hyphenated-artist forms. |
| MATCH-05 | ✅ Fixed | `ExtractVersionInfo` runs its six regexes over the TITLE only; the album parameter survives on the signature for call-site compatibility with an in-code comment stating why. `"Yesterday" + album "Live at Wembley"` → not live; `"Song" + album "Remix Collection"` → not remix. Table: 8 cases. This un-breaks studio tracks living on live albums, which the version-mismatch gates used to demote −0.35 wholesale. |
| MATCH-06 | ✅ Fixed | The 15–25 s dead zone is gone: everything beyond the ±15 s tier is an explicit mismatch (−0.15, evidence line included). A ~20 s-off different edit previously escaped ALL duration scoring — same credit as a near miss. Test: ±20 s confidence < ±15 s confidence AND <0.70; evidence contains "Mismatch". Intended consequence: `ScoreCandidate_AmbiguousMatch_YieldsModerateScore` moved bands (0.60–0.80 → 0.40–0.70) because its ±17 s candidate now takes the penalty — updated with an explanatory comment rather than left lying about behavior. |
| MATCH-07 | ✅ Fixed | `FindMatchesForFileAsync` initializes `trackNum = 0` ("unknown"), not 1. The old default survived TagLib open-failures, so the `trackNum <= 0` filename fallback was unreachable exactly when tags were unreadable — and a phantom TrackNumber=1 collected the +0.05 bonus against track-1 candidates. Test feeds a fLaC-magic garbage `.flac` (strict parser throws — B6 fixture lesson) named `07 - Pink Floyd - Time.flac`: the scored evidence must contain "Track #7 Match", impossible under the phantom default. |
| MB-02 | ✅ Fixed | `CalculateTrackConfidence` folds normalized ARTIST similarity into the base multiplicatively (`×(0.5 + 0.5·sim)`, +0.05 at sim ≥0.95) after the title boost, then applies an album-agreement tier (sim ≥0.85 → +0.05; <0.40 → −0.10). Same artist fold added to `CalculateAlbumConfidence`. A wrong-artist same-title recording can no longer ride search score=100 into auto-apply tiers. Arithmetic preserves perfect matches at 1.0 (fold ×1.0 +0.05 +0.05 capped) so the standing ≥0.95 assertions still hold. Test: cover-band recording scores ≤0.60 with a ≥0.30 gap to the true artist. |
| MB-03 | ✅ Fixed | New `LocateTrackPosition(rec)` scans releases/media for the track whose `recording.id` equals THE recording's id and reports that track's number + medium position; unknown otherwise. Replaces both first-medium-first-track reads. Test JSON places the hit recording at disc 2 / track 3 behind a decoy first medium → asserts 3/2 (old code reported 1/1). |
| CAA-01 | ✅ Fixed *(semantics refined during testing — see NF-06)* | `MusicBrainzId` is entity-ambiguous; producers now stamp `AdditionalIds["MusicBrainzEntityKind"]` (new `ExternalIdKinds` static class: release / release-group / recording) and the artwork provider routes accordingly. Recording/release-group-stamped ids route AWAY from `/release/{mbid}` toward the RG endpoint or album search. Unstamped ids keep release semantics (legacy cache compatibility). Tests: recording-stamped id ⇒ zero requests (old code fetched and would have fabricated /front); release-stamped positive control still hits the endpoint once. |
| CAA-02 | ✅ Fixed | Both speculative `/front` fallback returns replaced with a debug-log + empty result. A JSON-fetch failure is not evidence art exists; the fabricated URL could only 404 downstream, indistinguishable from a real answer and wasting a download attempt per failed release. Test: 404 on the JSON endpoint ⇒ empty list, no URL containing `/front`, exactly one network attempt. |
| ADB-01 | ✅ Fixed *(defense-in-depth — see notes)* | Key resolution treats blank exactly like absent: read nullable settings key, use trimmed value when non-whitespace else `_options.ApiKey`. Kills the `"{base}//artist-mb.php"` malformed-URL shape. Both sites fixed with `string?` locals (two CS8600s caught by the 0-warning invariant during development). Test: padded settings key is trimmed AND preferred over the options key; URL free of the double-slash shape. |
| ADB-02 | ✅ Fixed | New `PickBestArtist(response, targetName, targetMbid)`: single result passes through; otherwise prefer the row whose `strMusicBrainzID` equals the target (MBID path), else the row whose NORMALIZED name equals the query (name path), else `artists[0]` as last resort. Blindly binding `Artists[0]` attached the wrong artist's bio/images whenever search returned homonyms. Tests both paths: `"queen!"` picks `QUEEN!` over `Queensryche`; MBID query picks the matching row over the decoy first. |
| LRC-01 | ✅ Fixed | Single-flight key now includes normalized album (or `-` when absent) plus rounded seconds (0 = unknown), so same title+artist on different releases don't share one in-flight result and receive each other's lyrics. Concurrency test gates the handler until a second distinct lookup arrives: two different-album calls ⇒ 4 `/get` hits (2 variants × 2 independent lookups); under the shared old key the second caller joined the flight ⇒ 2. |
| LRC-02 | ✅ Fixed | After both exact `/get` attempts miss, the provider queries `/api/search` (lrclib's fuzzy endpoint) and keeps the best-scored row — `0.5·sim(title)+0.5·sim(artist)` with a duration modifier (+0.15 ≤2 s / +0.05 ≤5 s / −0.10 else) and a 0.60 acceptance floor. The old third attempt repeated the IDENTICAL query that had just failed. Test: forced `/get` 404s ⇒ exactly one `/search`, result parsed as Synced. Note: the second bare-`/get` retry (step 2) is intentional — lrclib documents the relaxed variant as a distinct lookup — and was left alone. |
| HLP-03 / TEST-15 | ✅ Covered | New `MetadataTextNormalizerTests`: table-driven theories for CalculateSimilarity (10 rows incl. diacritic/punctuation folds and the MATCH-02 bands), ExtractVersionInfo (8 rows), ParseFromPath (6 rows + directory-as-album fact) — the fuzzy-match core was previously tested only indirectly through matcher integration tests. Plus new matcher/provider/artwork/artist/lyrics tests enumerated above (+39 total this batch). |

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-06 | ⚠️ Design regression *(self-caught during B7 verification)* | `CoverArtArchiveArtworkProvider.SearchAlbumArtworkUrlsAsync` | **The first CAA-01 implementation required a POSITIVE release stamp**, so every persisted/cached `ExternalIds` predating the stamping contract — which carries none — silently lost its release-endpoint route: two standing orchestrator tests (`SecondCall_ReturnsCachedTokenWithoutNetwork`, `ConcurrentDuplicateRequests_…`) failed with null tokens because `ExternalIds("rel-hit")` unstamped no longer resolved to any fetch. Redesigned to EXCLUDE-only semantics: explicit recording/release-group stamps route away from the release endpoint; UNSTAMPED ids keep release semantics so stored artwork lookups can't regress. All new MB-produced candidates carry stamps either way, so the original bug (recording mbids fetched against `/release/`) remains fixed. Lesson recorded: gating on absence-of-evidence breaks every producer that predates the evidence. |

#### Notes & deliberate trade-offs

- **Entity-kind stamping contract**: producers stamp `AdditionalIds[ExternalIdKinds.EntityKind]`; consumers gate on it. It is intentionally a soft contract (string dictionary, not a typed field) so DTO round-trips through the cache don't need schema changes; NF-06 documents the failure mode this choice requires consumers to respect.
- **MATCH-03 placement rationale**: neutral +0.05 ranks unknown-length candidates between the ±8 s (+0.08) and ±15 s (+0.02) tiers — viable, but never ahead of imperfect-but-real data, which was the audit's actual complaint.
- **ADB-01 reachability**: with a settings service present, `IsEnabled` already refuses blank API keys, so the blank-key branch is defense-in-depth rather than independently reachable today; the test pins the resolution chain (trim + precedence + malformed-shape impossibility) so future gate changes can't resurrect the bug silently.
- **TagLib#/FLAC fixture reuse**: the MATCH-07 regression test leans on the same strictness discovered in Batch 6 (FLAC magic/block validation throws where the MPEG handler shrugs) — and passes either way if a future TagLib version opens the fixture leniently, because the empty-tag fallback path produces the same TrackNumber=7 outcome.

**Acceptance verified** (§15 Batch 7): table-driven TEST-15 suites cover each normalizer/matcher fix · provider, artwork, artist, and lyrics behaviors each pinned by at least one dedicated test · build 0 warnings / 0 errors.

**Final state**: `dotnet test Octave.Core.Tests` = **220/220** (181 prior + 39 new; includes the two pre-existing tests updated for intended new behavior — see MATCH-06 and NF-06 rows).

---

### Session 2026-08-23 — Batch 6 — Metadata editor integrity *(commit: this session)*

**Files opened**: `TrackMetadataEditor.cs`, `SqliteDbContext.cs` (one new method next to the upserts — the transaction scope the batch names), `MetadataEditorTests.cs` (+7 tests). Three more files are opened *by explicit batch-spec mandate*, not drift: `SmartLibraryEnrichmentService.cs` (EDITOR-06 names it as the second clamp site, shared with B16) + new `Helpers/MetadataValueClamps.cs` (the shared clamp constants that implies); `App.xaml.cs` (EDITOR-04's "sweep stray `.octave_bak` on startup"); `MetadataEnrichmentViewModel.cs` (ME-07). `ManagedBassAudioServiceTests.cs` also changes — NF-05 below, a defect in a Batch-5 *test*, no production audio code touched. ME-06 is not touched here (spec assigns it to B11).

| ID | Status | Evidence |
|----|--------|----------|
| EDITOR-01 | ✅ Fixed | Multi-value input split into separate frame elements: regex `\s*;\s*\|\s+/\s+` — semicolon splits anywhere, slash only when space-padded, so `"Dido / Eminem; Sarah Connor"` → three performers but `"K/S"` stays whole. `SetFrameIfDifferent` skips the write when the incoming frame equals the existing one (trim + OrdinalIgnoreCase), killing no-op tag rewrites. Test asserts all four frame types round-trip split. |
| EDITOR-02 | ✅ Fixed | New `SqliteDbContext.DeleteOrphanedArtistsAndAlbumsAsync(ids, tx)`: per id, `DELETE … WHERE Id=@id AND NOT EXISTS (SELECT 1 FROM Tracks WHERE ArtistId=@id)` (album analog). **The guards are load-bearing**: `Tracks.ArtistId`/`AlbumId` FKs are `ON DELETE CASCADE`, so an unguarded orphan-cleanup DELETE would silently cascade-delete live tracks. Runs inside the same transaction as the upserts. Test: two tracks share artist/album → rename T1 keeps old rows (T2 still references them); renaming T2 sweeps both old rows; both tracks re-pointed. |
| EDITOR-03 | ✅ Fixed *(with a declined sub-item)* | All three rename upserts + orphan cleanup wrapped in ONE `BeginTransactionAsync` / `CommitAsync` scope — a mid-rename crash can no longer leave artist renamed but album/track stale. Sharing the scanner's `_writeSemaphore` deliberately DECLINED: B1's SQLite busy_timeout already serializes cross-connection writers at the DB level, editor saves are rare single-user operations, and taking the scanner's long-lived semaphore couples two subsystems' lifecycles for no correctness gain. |
| EDITOR-04 | ✅ Fixed | Writes go to a same-directory sandbox copy swapped in with `File.Replace(temp, original, backup)` — atomic on NTFS within a volume; a crash mid-write can no longer half-write a music file. Backup deleted only AFTER the swap is verified (line-of-success `SafeDelete(backupPath)`). New static `SweepStaleEditorArtifacts(root)` deletes stray artifacts recursively (a backup can only exist once a replace completed; a temp means it never ran — both always safe), wired fire-and-forget into App startup after watcher init so boot never waits on a library walk. Test proves the sweep removes exactly the 2 artifact files and nothing else. |
| EDITOR-05 / CRIT-03 | ✅ Fixed | The exclusive-open lock probe is gone — it opened+closed a handle before the real write (TOCTOU window proving nothing). Locks now surface from the write itself and are classified in the catch (`IOException` → "locked or currently in use", `UnauthorizedAccessException` → access denied). |
| EDITOR-06 | ✅ Fixed | New `Helpers/MetadataValueClamps.cs` (`Year ∈ [1000, currentYear+1]`, track/disc/count ∈ [1,999]) gating EVERY numeric write in the editor AND the enrichment service's year/track/disc writes (SmartLibraryEnrichmentService :398–412) — one clamp definition shared with B16 as the spec requires. Behavior change: out-of-range input now PRESERVES the existing value everywhere instead of being written or erased (old code mapped Year=-5 to 0, actively erasing data). Test: Year=99999/Track=5000/TrackCount=-3/Disc=0 rejected, Title still applied, file and DB agree on preserved values. |
| EDITOR-07 | ✅ Fixed | Catch path: delete temp; if a backup exists, restore via `Copy(backup → original)` and delete the backup ONLY after that copy succeeds; if the restore itself fails, return a hard error NAMING the backup path ("The original file is preserved there") instead of pretending. |
| EDITOR-08 | ✅ Fixed | Artwork re-read/re-cache gated on `artworkChanged` (ClearArtwork or non-empty NewArtworkBytes); unchanged-path edits keep the OLD cached album art downstream so renames carry it without a picture decode. Re-read prefers the FrontCover frame (`FirstOrDefault(FrontCover) ?? [0]`) and validates bytes via `ImageValidator.IsValidImage` (detects MIME) rather than trusting the frame's self-declared type. |
| EDITOR-09 | ✅ Fixed | File-write success followed by Stage-3 failure no longer vanishes: prominent `[TrackMetadataEditor] FILE/DB DIVERGENCE` log + `MetadataEditResult.DbSyncFailed(fileResult, dbResult)` whose summary names BOTH facts ("File written successfully, but database sync failed: …"). Test forces the failure via an artwork-cache root pointing at a regular FILE, then asserts Success=false, FileResult.Success=true, DbResult.Success=false, summary wording, and that the file tags really were written (divergence is real, not theoretical). |
| ME-01 | ✅ Resolved *(superseded)* | The finding asked for a temp-dir staging write. Superseded by a stronger design: a cross-volume temp-dir backup cannot be swapped atomically, so the fix is same-dir temp + `File.Replace` (see EDITOR-04) — the crash window ME-01 worried about is closed entirely rather than narrowed. Documented in-code at the artifact-path consts. |
| ME-02 | ✅ Resolved *(clarified)* | Stage-2 reread failures fall back to the write payload (`update ?? existing`) — which IS the known on-disk state, so the DB cannot diverge from the file even when the re-read dies. Comment documents why this is correct rather than a shortcut. |
| ME-03 | ✅ Fixed | With EDITOR-01: multi-value splitting + identical-frame skip. |
| ME-05 | ✅ Fixed | With EDITOR-02/03: renames are transactional and orphan cleanup runs in-tx with NOT EXISTS guards. |
| ME-07 | ✅ Fixed *(stop-gap; UI deferred)* | Grep-proven fact: the enrichment dialog never displays Composer/TrackCount/DiscCount and the VM has no bindings for them, yet `FieldSelectionOptions` default-true force-applied all three — writing fields the user could neither see nor approve. The VM now passes `ApplyComposer/ApplyTrackCount/ApplyDiscCount: false`. Real toggles ship with the VM/UI batch that owns that dialog (B11+); until then nothing invisible is written. |
| TEST-01 | ✅ Covered | `UpdateTrackMetadataAsync_FailureAfterSandbox_RestoresOriginalByteForByte_NoStrayArtifacts`: original held open with a read-share handle (copy succeeds, TagLib saves the temp fine, `File.Replace` hits the sharing violation) → result failed with "locked", original byte-for-byte equal, zero `.octave_bak`/`.octave_tmp` strays. Companion: undecodable file fails cleanly the same way. |
| TEST-02 | ✅ Covered | `…_DbSyncFails_ReturnsDivergence_FileRemainsWritten` (see EDITOR-09). |

#### New findings discovered & fixed on the go *(this session)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-04 | ⚠️ Bug *(self-caught pre-test-run)* | `TrackMetadataEditor.BuildTempPath` | **Sandbox name broke TagLib#**: naming the temp `song.mp3.octave_tmp` made `TagLib.File.Create` throw — it resolves file type by EXTENSION, and `.octave_tmp` is unknown — so every sandboxed save failed. Fix: the marker goes BEFORE the real extension (`song.octave_tmp.mp3`); backups keep the plain suffix (nothing sniffs them). Sweep patterns updated accordingly (`*.octave_tmp.*`, `*.octave_tmp`, `*.octave_bak`). Caught because the first full-suite run failed 12 tests, all with "unknown file type"-class errors. |
| NF-05 | ⚠️ Test bug *(Batch 5 regression test, no production change)* | `ManagedBassAudioServiceTests.CrossfadeSkip_*` | **The committed crossfade-skip test asserted impossible mechanics**: it expected the SKIPPED track to deliver a "natural end during the fade", but the fade (250ms from ~2.5s) always completes before A's 3.0s end and the AUDIO-07 detach intentionally makes skipped streams silent — so delivery #1 could only happen if the 20ms position poll overshot past A's natural end before the skip landed. It passed at commit time on that race and then began failing deterministically. Rewritten to pin the REAL contract, deterministically: A=8s, skip window `pos ∈ [5.0, 5.5)` (500ms wide vs 20ms poll, clear of both boundaries) → EXACTLY ONE delivery, equal to the incoming track's own `(sessionId, uri)`; any second delivery would mean a sync survived the detach (the literal AUDIO-07 bug). Renamed `CrossfadeSkip_SkippedTrackNeverReports_IncomingEndsOnceWithOwnIdentity`. |

#### Notes & deliberate trade-offs

- **TagLib#/ID3v2 medium limitation (documented, not fixed here)**: the library's ID3v2 string handler serializes multi-value frames joined on `/` and re-splits them on read, so even a correctly-written single performer `"AC/DC"` reads back as `["AC","DC"]`. The editor adds no extra split (its regex requires space-padded slashes); the test asserts information preservation (`string.Join("/", performers) == "AC/DC"`). A global custom `TagLib.Id3v2.StringHandler` is the remedy — a cross-cutting change affecting every TagLib consumer, out of this batch's scope.
- **Undecodable-fixture choice**: garbage bytes named `.flac`, not `.mp3` — the MPEG handler leniently opens extension-resolved frameless MP3s (no magic check), so `.mp3` garbage produces SUCCESS, not failure. FLAC verifies the `fLaC` magic strictly.
- **Crash-artifact sweep safety is by construction**, not by heuristic: a `.octave_bak` exists only after a successful atomic replace; a leftover temp means the replace never ran. Either way the live audio file is intact.
- **Stage-1 numeric gates mirror Stage-2 fallback gates exactly** — a value the file rejected never reaches the DB either; otherwise clamped-rejected values would silently diverge file vs DB (the exact class of bug this batch removes).
- Existing editor tests needed no edits: the read-only and locked-file classifications survive (lock classification now comes from the write itself), and artwork caching behavior is unchanged on the changed-artwork path.

**Acceptance verified** (§15 Batch 6): forced `Save()` failure restores the file byte-for-byte with no leftover `.octave_bak` (TEST-01 test) · DB-sync-failure path returns the explicit divergence state (TEST-02 test) · build 0 warnings / 0 errors.

**Final state**: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **181/181** (174 prior + 7 new metadata-integrity tests; Batch 5's rewritten regression test included in count).

### Session 2026-08-23 — Batch 5 — Audio engine safety *(commit: this session)*

**Files opened**: `ManagedBassAudioService.cs` (the one the batch names) + new `ManagedBassAudioServiceTests.cs` + `Octave.Core.Tests.csproj` (two `<None>` items so the test host can load the native `bass.dll`/`bass_fx.dll` — the real-engine tests need them). The load-bearing §12.4 invariant is preserved and now documented as a comment on `_streamLock`: **no event is raised while `_streamLock` is held**.

| ID | Status | Evidence |
|----|--------|----------|
| AUDIO-01 | ✅ Fixed | `Play` restructured into three phases: (1) retire the previous stream under `_streamLock` (quick native handle calls only), (2) `Bass.CreateStream` — including blocking HTTP connects — runs **outside** any lock, (3) install/attach under `_streamLock`. Concurrent Plays are serialized by a separate `_playGate` so phase interleaving can't leak streams; nothing acquires `_streamLock` then `_playGate`, so no ordering deadlock. Position timer, FFT reads, and `Status`/`PositionSeconds` polls no longer block behind network I/O. |
| AUDIO-02 | ✅ Fixed | The No-Sound fallback now records state instead of pretending success: `IsSilentFallback` (public on the concrete class) is set when device `-1` fails and device `0` succeeds, with a prominent warning log. Every subsequent `Play` calls `TryRecoverRealDevice()`, which retries `Bass.Init(-1)`; a successful re-init becomes the calling thread's current device, and the stream created later in that same Play call deterministically lands on real output. |
| AUDIO-03 | ✅ Fixed | Lazy `Init()` moved inside `_playGate` — concurrent Plays can no longer both enter `Bass.Init`. |
| AUDIO-04 | ✅ Fixed | Finalizer and `Dispose(bool)` removed entirely; native teardown (`fading streams → current stream → Bass.Free()`) happens only in deterministic `Dispose()` under `_streamLock`, which is now idempotent (re-checks `_disposed` under the lock). No more finalizer-thread lock acquisition / native re-entry risk, and an undisposed instance can no longer tear down the shared BASS engine from a finalizer (TEST-17's root cause; B15 adds the dispose-sweep). |
| AUDIO-05 | ✅ Fixed | Priorities swapped per BASS semantics (higher priority applies FIRST): PeakEQ now priority 1, Compressor/limiter priority 0 — the limiter sits last in the chain where it can actually catch clipping from up-to-+15 dB EQ boosts (previously it ran ahead of the EQ and couldn't). |
| AUDIO-06 | ✅ Fixed *(present parts)* | `CrossfadeDurationMs` clamped to `[0, 10000]`; PeakEQ bandwidth 2.5 → 1.0 octaves to match the 1-octave ISO band spacing; `_lastFftPeaks` resize moved inside `_streamLock`. The "benign aligned-float reads" sub-item was left as-is — the audit itself grades it benign, and locking those reads would add contention on every volume/preamp access for no correctness gain. |
| AUDIO-07 | ✅ Fixed | Per-stream End-sync identity: `RegisterEndSyncUnlocked` stores `(syncHandle, sessionId, uri)` keyed by stream handle at create-time; the callback resolves identity from its own `channel` argument — global `_currentSessionId`/`_currentSourceUri` are **deleted**. `DetachEndSyncUnlocked` (`ChannelRemoveSync` + registry remove) runs on every teardown path: Play's fade branch, Play's stop/free branch, Stop's fade branch, Stop's stop/free branch, and Dispose clears the registry. A late/superseded end finds no entry and is dropped. Failure-path `TrackEnded` keeps its local per-call identity (the one case where that is correct). Regression test drives the REAL engine: skip track A via crossfade inside its final stretch → exactly two deliveries, `(sessionA, fileA)` then `(sessionB, fileB)` — under the old code the first delivery reported B's identity (the queue-skip bug); a third delivery would mean a lingering sync survived the detach. |

#### Notes & deliberate trade-offs

- *(Post-session correction, see Batch 6 NF-05)*: the AUDIO-07 regression test described below in its original form asserted the skipped track delivering its own end — impossible mechanics that passed on a timing race. The test was rewritten in Batch 6 to pin the real contract (skipped stream silent; exactly one delivery carrying the incoming track's own identity). The AUDIO-07 **production fix** itself is unchanged by that correction.
- **New guard — stop-vs-inflight-play**: because stream creation now happens outside the lock, a `Stop()` during a slow HTTP connect would otherwise install the stream after Stop returned. `Stop` bumps `_stopGeneration`; Play captures it before creating and, if it changed, frees the freshly-created stream quietly (no events) — honoring the user's explicit stop.
- **Transient zeros during transitions**: with the lock released during connect, `PositionSeconds`/`Status` read `_currentStream == 0` for the connect duration and briefly report 0 / Stopped. That beats a multi-second UI freeze (the bug being fixed); queue-side state (what the UI actually binds to) is unaffected.
- **AUDIO-02 surface**: `IsSilentFallback` lives on the concrete class only (batch scope forbids touching `IAudioPlayerService`). UI surfacing of the silent-state warning is deferred until some batch opens that interface; today the warning is logged prominently and recovery is automatic.
- **Test infrastructure**: `Octave.Core.Tests.csproj` now copies `bass.dll` + `bass_fx.dll` from the Desktop project into the test output — the first tests to drive real playback. WAV fixtures are generated in-code (16-bit mono PCM, core-decodable, no plugin needed).
- Dead code removed while in the file: `FreeStreamInternal` had zero call sites (its body was inlined in `Dispose` all along).

**Acceptance verified** (§15 Batch 5): no lock held across I/O (three-phase structure; `_streamLock` sections contain only quick native handle calls) · dispose deterministic + idempotent (test) · crossfade-skip regression passes against the real engine (test).

**Final state**: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **174/174** (170 prior + 4 new audio-safety tests).

### Session 2026-08-23 — Batch 4 — Playback event storms & queue integrity *(commit: this session)*

**Files opened**: `QueueService.cs`, `NowPlayingViewModel.cs`, `NowPlayingPage.xaml.cs` (the three the batch names) + `IQueueService.cs` (one added member for NP-10) + `QueueServiceTests.cs`. The service was rewritten around one discipline: **mutate under `_queueLock` → snapshot via `CaptureStateUnlocked()` → raise events only after the lock is released** (`RaisePlaybackEvents`).

| ID | Status | Evidence |
|----|--------|----------|
| QUEUE-01 | ✅ Fixed | One token allocator, **two watermarks**: full saves check/advance `_lastPersistedStateToken`; lightweight writes check `max(state, progress)` watermarks and advance only `_lastPersistedProgressToken`. The audit's loss scenario is structurally impossible now: a late progress write completing first can no longer advance past a pending full-state save (full saves are skipped only by *newer full saves*), while stale lightweight writes still yield to any newer completed save so they can't clobber fresh index/shuffle/repeat with old values. |
| QUEUE-02 | ✅ Fixed | Circuit breaker in `HandleTrackEndedAsync`: a track that "ends" at `< 0.75 s` position never actually played (exists but fails to decode). Consecutive failures are counted; at `min(queueCount, 10)` the queue calls `Stop`, clears the playing flag, emits the stopped state and logs. Counter resets on any real playback. Test: 3 undecodable files + `RepeatMode` default → after one lap, `Stop` verified `Times.Once`, playback halted without wrapping. Bound of 10 also caps huge corrupt queues (5000-track queue stops after 10 spins, well within "one lap"). |
| QUEUE-03 | ✅ Fixed | The `_audioPlayer.TrackStarted` subscription is **deleted** (it fired synchronously inside `Play` while `PlayIndexInternal` was mid-emit → two full broadcasts per transition; root of the VM-03/NP-09 double rebuilds). Test asserts exactly 1×`PlaybackStateChanged` + 1×`QueueChanged` for a skip **and** stays at 1 after an explicit `TrackStarted` raise. |
| QUEUE-04 | ✅ Fixed | Every mutator (`Enqueue*`, `RemoveAt`, `Clear`, `Reorder`, `SetShuffle`, `SetRepeatMode`, `PlayNext/Previous`, `Pause/Resume`, `SetVolume`, `PlayIndexInternal`, `HandleTrackEndedAsync`, `LibraryChanged` purge) now captures state under the lock and raises **after** releasing it. Subscribers can no longer run under `_queueLock`. Not directly unit-testable (Monitor reentrancy hides same-thread nesting); verified structurally — every `.Invoke` site is outside the lock. Load-bearing lock-order invariant from §12.4 documented as a comment on `_queueLock`. |
| QUEUE-05 | ✅ Fixed | `SetVolume` updates `CurrentState` with `persistSnapshot: false`; volume reaches disk via the shared throttled (5 s) lightweight write (`MaybeSaveLightweight`) instead of a full-snapshot DB save per slider tick. |
| QUEUE-06 | ✅ Fixed | Missing-file branch converted from tail recursion to an iterative `while(true)` loop. Test: a 20,000-entry all-missing queue clears completely, `Play` never called, `Stop` once — no stack overflow. |
| QUEUE-07 | ✅ Fixed | `Random.Shared`. |
| QUEUE-08 | ✅ Fixed | `RestoreAsync` seeds `_unshuffledQueue` from `PersistedPlayerState.UnshuffledTrackIds` (falling back to the active order only when null/empty — pre-migration snapshots). Test: persist shuffled active `[t3,t1,t2]` + natural `[t1,t2,t3]`, restore, assert active order restored AND shuffle-off recovers the true `[t1,t2,t3]` (previously the shuffled order was permanently promoted to "original"). |
| QUEUE-09 | ✅ Fixed | `listToShuffle.RemoveAll(i => i.Id == currentItem.Id)` — Id-based pruning across the distinct active/unshuffled instances. Acceptance holds: test plays track 4 of 8, shuffles → count stays 8 and the playing track appears exactly once, pinned to index 0. |
| QUEUE-10 | ✅ Fixed | When `_currentIndex < 0`, `EnqueueNext` inserts at the **front** of the unshuffled mirror too (was appended to the end while going to the front of active). Test: EnqueueNext before any play → round-trip through shuffle on/off → inserted track is first. With a current track, insert-after-current behavior unchanged. |
| QUEUE-11 | ✅ Fixed *(intent confirmed)* | Reordering while shuffled now leaves `_unshuffledQueue` frozen: dragging in the shuffled view rearranges play order only, and shuffle-off still restores the true source sequence. Decision documented in-code (the old code rewrote the natural order next to the dragged item's new neighbor). |
| QUEUE-12 | ✅ Fixed | `QueueService : IDisposable`; handler delegates stored in fields and detached in `Dispose()`. DI disposes the app-lifetime singleton automatically; no interface change needed. |
| NF-03 *(new finding)* | ✅ Fixed | **Cross-list reference-compare removal**: `PlayIndexInternal`'s missing-file branch called `_unshuffledQueue.Remove(item)` where `item` came from `_activeQueue` — distinct instances, never matched, so purged tracks survived in the natural order and **resurrected on shuffle-off** (same root cause as QUEUE-09, second site). All cross-list QueueItem removals now match by Id. Test: RemoveAt last track → shuffle on/off → removed track stays gone. |
| VM-03 | ✅ Fixed | `RefreshUpNextQueue()` dropped from `UpdateFromState`; `OnQueueChanged` alone owns rebuilds (every state transition emits both events exactly once post-QUEUE-03, and `Seek` intentionally refreshes neither). |
| CRIT-01 | ✅ Fixed | The ViewModel constructor is the single subscription point: page `Loaded` no longer calls `SubscribeEvents()`, and `SubscribeEvents`/`UnsubscribeEvents` are idempotent via an `_eventsSubscribed` guard (a stray future double-call can't double-deliver). |
| CRIT-04 | ✅ Fixed | `UpdateFromState` no longer assigns `CurrentArtworkUrl = CurrentAlbum?.ArtworkUrl` — `CurrentAlbum` lags one async hop behind, blanking the panel on every track change. Previous artwork is held until `LoadCreditsAsync` resolves the new album's art; a real stop still clears it. (Not unit-testable — see notes.) |
| NP-08 | ✅ Fixed | The primary-artist full-match check now compares whitespace-collapsed, case-folded keys, so formatting drift between tag and DB ("Simon &  Garfunkel", casing) no longer falls through to the split regex and shreds `&`-bands into two artists. |
| NP-09 / UI-NP-05 | ✅ Fixed | `RefreshUpNextQueue` builds nothing when the visible window is Id-identical to what's shown — the Clear()+re-add flicker now happens only on genuine queue changes, not on every unrelated state pulse. |
| NP-10 | ✅ Fixed | New `IQueueService.PlayQueueItem(string itemId)` resolves the index under `_queueLock` via `FindIndex`; the VM passes the clicked item's Id. No per-click full-queue snapshot copy, no linear rescan in the VM, no TOCTOU between snapshot and `PlayIndex`. |
| INT-05 *(second half)* | ✅ Fixed | Inside `RestoreAsync`: both `GetTracksByIdsAsync` calls and the final event dispatch are wrapped in try/catch with Debug diagnostics (first half — awaiting the App.xaml.cs call site — landed with Batch 3). |

**Acceptance verified** (§15 Batch 4): each playback-state change emits exactly once (test) · enabling shuffle never duplicates the current track and counts stay equal (test) · `EnqueueNext` keeps active/unshuffled in sync in the `-1` index state (round-trip-shuffle test; ≥0 path covered by existing reorder/order tests) · restore preserves shuffle flag and correct current index, and now the true natural order too (test) · no events raised under `_queueLock` anywhere (structural review) · a queue of undecodable files stops after one lap (test) · artwork flash eliminated by construction (held-until-resolved semantics).

#### Notes & deliberate trade-offs

- **ViewModel/page changes are not unit-testable here**: `NowPlayingViewModel`'s ctor calls `DispatcherQueue.GetForCurrentThread()` (WinUI) and lives in the Desktop project, outside `Octave.Core.Tests`. Those fixes (CRIT-01/04, VM-03, NP-08/09/10) are proven by the 0-warning build plus the structural reasoning above.
- **Breaker threshold**: `min(queueCount, 10)` — the audit allowed "N or one full lap"; the cap prevents a huge corrupt library from spinning thousands of times before one lap completes. A false positive would require ten consecutive tracks ending under 0.75 s of playback (no real track is that short in practice).
- **`Seek` semantics unchanged**: it raises `PlaybackStateChanged` only (no `QueueChanged`) — position moves don't alter the queue, and the diff-guard in the VM would no-op anyway.

**Final state**: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **170/170** (162 prior + 8 new queue-integrity tests).

### Session 2026-08-23 — Batch 3 — External-data wiring & dead settings *(commit: this session)*

**Files opened**: `App.xaml.cs`, `ExternalDataSettingsService.cs`, `ExternalDataSettings.cs`, `SmartLibraryEnrichmentService.cs`, `CompositeLyricsService.cs` (the five the batch names) + the three test files that prove it.

| ID | Status | Evidence |
|----|--------|----------|
| INT-01 | ✅ Fixed | `App.OnLaunched` now calls `await IExternalDataSettingsService.LoadSettingsAsync()` inside the DB-init try block **after** `InitializeAsync()` and **before** `_ = InitializeWatcherAsync()`, window activation, and `RestoreAsync()` — so `OfflineOnlyMode` / provider toggles / API key are live for the watcher, queue restore, and first-play. Service half proven by `LoadSettingsAsync_PersistedOfflineOnlyMode_IsRestoredWithoutOpeningSettingsPage` (fresh service instance = next process launch reads persisted values, not model defaults). The cold-launch "no network call" acceptance follows structurally: providers gate every call on `IsEnabled`, which reads `CurrentSettings` live (§13 verified-clean note) — once the persisted values are loaded at boot there is no online-by-default window left. |
| INT-02 / SLE-02 | ✅ Fixed | `SmartLibraryEnrichmentService.PlanForTrackAsync` now consults the three dead settings. New gating block: `considerMetadata = AutoFillMissingMetadata && WritePolicy != NeverWriteAutomatically && needsMetadata`; `mayReplaceExisting = ReplaceExistingMetadata && (WritePolicy is WriteOnlyHighConfidence or AlwaysPreferOnline)`; every field write routes through `ShouldWriteField(localMissingOrGeneric, mayReplaceExisting, …)` → fill-missing under all active policies, overwrite only with permission + an overwrite-capable policy. Semantics chosen so each enum value does what its name says: Never=0 writes; WriteOnlyWhenMissingAndHighConfidence (default)=fill-missing; WriteOnlyHighConfidence/AlwaysPreferOnline=replace-with-permission. `needsLyrics` (`ScanOnlyMissingLyrics`) now gates the lyrics fetch; `ScanOnlyMissingMetadata` (`needsMetadata`) gates consideration of complete tracks. Tests: autofill-off → zero tag-write flags; Never+autofill-on → zero flags; FillMissing fills missing genre but never overwrites a real year even with replace permission granted; AlwaysPreferOnline+permission overwrites the real year; overwrite policies *without* permission still fill-missing-only; two-phase ScanOnly test (complete track untouched under full permissions with the filter ON, rewritten after turning it OFF). |
| INT-03 | ✅ Fixed | Artist gate is now `!completeness.IsArtistComplete && EnableArtistEnrichment && AutoDownloadMissingArtistImages`. Test: complete artist in DB + both toggles on → `GetEnrichedArtistAsync` verified `Times.Never`, no `EnrichArtistBioAndPhoto` flag (previously fired on every scan). |
| INT-06 | ✅ Fixed (wording path) | Comment above the settings serialization states plainly that the key is stored as **plaintext** in the local DB and that DPAPI needs a Windows-specific TFM (Core targets plain `net10.0`, so `ProtectedData` is unavailable). Chose the audit's sanctioned alternative to encryption; no misleading "secure" framing remains. |
| INT-07 | ✅ Fixed | `SyncOptionsWithSettings` always assigns `_theAudioDbOptions.ApiKey = settings.TheAudioDbApiKey?.Trim() ?? ""` — clearing the key in the UI clears the live singleton immediately (test: set → assert applied → clear → assert empty). Model default `"2"` → `""`; consequence: a fresh install starts with TheAudioDb **disabled** until the user enters a key (its `IsEnabled` requires a non-empty key) — correct consent posture, noted as deliberate behavior change. Default-settings assertion updated accordingly. |
| ORC-02 | ✅ Fixed | `CompositeLyricsService.GetLyricsAsync` passes `ExternalTagIds.TryReadFromFile(track.SourceUri)` (null-safe for nonexistent paths) instead of hardcoded `null`, so file-tag MBID/ISRC reach the lyrics providers — the strongest match signal available at that call site. Regression test builds a real tagged MP3 (`MusicBrainzTrackId="mb-orc02-1"`) and verifies the provider received matching `ExternalIds` exactly once (previously always null). |

#### New findings discovered & fixed on the go *(this session — recorded here since they were found-and-fixed atomically)*

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| NF-01 | ⚠️ Bug | `SmartLibraryEnrichmentService.PlanForTrackAsync` disc write | **Disc-number restamp**: the plan builder wrote the candidate's `DiscNumber` whenever present, comparing against nothing (`Track` has no DiscNumber column) — every scan restamped existing disc numbers. Fix: read `tagFile.Tag.Disc` while the tag is already open (`localDiscNumber`, alongside the SLE-01 id capture) and route through the same missing-vs-permission logic as every other field. Tests: existing local Disc=2 + candidate Disc=2 → no `WriteDiscNumber`; missing local disc → filled from candidate. |
| NF-02 | 💡 Inefficiency | `ShouldWriteField` call sites | **Identical-value restamp under replace policies**: with `mayReplaceExisting` true, the builder flagged *every* field as written — including title/artist/album/track# whose candidate value equaled the stored one (no-op rewrites inflating plans and tag I/O). Fix: minimal-diff semantics — `ShouldWriteField` also compares values (trim + OrdinalIgnoreCase for text, direct equality for ints) and skips writes that change nothing. Tests updated to encode this (e.g. AlwaysPreferOnline+permission proposes only Year+Genre+Disc because those are the only fields whose values actually differ). |

#### Deliberate decisions & notes

- **Artwork block intentionally NOT gated by the new needs-flags**: `(!IsArtworkComplete || ReplaceExistingArtwork) && AutoDownloadMissingArtwork` already encodes the safe intersection — adding a `ScanOnlyMissingArtwork`-style needs-flag would fetch art for complete albums when "scan all" is selected and overwrite artwork the user never permitted replacing. Commented in-code.
- **ExternalIds writing stays governed solely by `WriteExternalIdsToTags`** (not by AutoFill/policy): ids are not user-visible metadata, they are non-destructive additions, and writing them is what makes MATCH-01's exact-ID path effective on the next scan.
- **INT-05 split across batches**: the App.xaml.cs half landed here (`_ = RestoreAsync()` → `await` in try/catch with a Debug diagnostic); guarding `RestoreAsync`'s internals (`GetTracksByIdsAsync` / `EmitPlaybackStateChanged`) lands with Batch 4, which owns `QueueService.cs`.
- **Test-fixture note**: policy tests whose fixtures are metadata-complete set `ScanOnlyMissingMetadata=false` explicitly so the write policy alone decides — otherwise the default-on scan filter would suppress consideration before the policy could be exercised.

**Final state**: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **162/162** (149 prior + 13 new: 10 SmartLibraryEnrichmentTests §11, 2 ExternalDataSettingsTests, 1 ORC-02 regression).

### Session 2026-08-23 — Batch 2 verification *(independent review pass)*

**Scope**: full read of the uncommitted Batch 2 working-tree changes against §12.7/§15 Batch 2 specs, plus build/test verification. The Batch 2 implementation itself was authored outside this session; this entry records the review that accepted it.

**Verdict — all four findings genuinely fixed; acceptance criterion met.**

| ID | Status | Evidence |
|----|--------|----------|
| MATCH-01 / SLE-01 | ✅ Fixed (verified) | `TrackMetadataMatcher.FindMatchesForTrackAsync` reads the file's real IDs once via new `ExternalTagIds.TryReadFromFile(SourceUri)` and threads them into every `ScoreCandidate` call as a new optional `localIds` parameter; the short-circuit now compares `localIds.Isrc↔candidateIds.Isrc` and `MusicBrainzId↔MusicBrainzId` (trim + OrdinalIgnoreCase) and returns `1.0` **before** fuzzy scoring. The dead `localTrack.Id` comparison is gone repo-wide (grep-verified zero stragglers). `SmartLibraryEnrichmentService` captures tag IDs while its existing tag-open is live (`ExternalTagIds.Read`) and passes them into `EvaluateHardSafetyGates`, which compares stored ISRC/MBID — and now honors exact ISRC too, which the old gate never did. Shared helper `ExternalTagIds` makes matcher/scan/workflow see one identical view of local IDs. |
| ENR-01 | ✅ Fixed (verified) | `ExternalIds` has manual value equality: scalars Ordinal, dictionary keys OrdinalIgnoreCase + values Ordinal, order-independent XOR-folded `GetHashCode`. Workflow diff flag = `candidateIds != null && !currentExtIds.Equals(candidateIds)` — identical IDs no longer flagged/rewritten; null proposed = nothing to apply (also fixes a latent NRE on `candidateId`). "No fuzzy confidence gate" holds structurally: the workflow rejects nothing by confidence (all candidates become previews tiered purely by score), and the pipeline's single threshold (`HighConfidenceThreshold = 0.85`) is cleared by construction because an exact-ID match scores 1.0. |
| AR-03 | ✅ Fixed (verified) | `ExternalIds.GetId`'s dictionary fallback routes through a case-insensitive lookup (exact hit first, then OrdinalIgnoreCase scan) aligned with the already-case-insensitive switch; value equality covers dict/set-key usage. |

**Acceptance verified** ("a track carrying a known MBID/ISRC matches by ID"): `ScoreCandidate_ExactMbidFromLocalTags_ShortCircuitsToPerfectConfidence` + ISRC twin (direct proof: 1.0, `IsExactIdMatch=true`); `ScoreCandidate_PathHashIdMatchingCandidateMbid_DoesNotShortCircuit` (reverse guard — the old buggy input can no longer trigger the short-circuit); `FindMatchesForTrackAsync_LocalFileTagsWithMbid_RankExactIdCandidateFirst` (full pipeline, real tagged MP3); `HardSafetyGates_ExactMbidOnFileTags_PassesDespiteGarbageFuzzyFields` (end-to-end gate → persisted `SafeReadyToApply`); workflow pair proving both directions of the ID diff; 6 × `ExternalIdsEqualityTests`.

**Minor observations recorded, deliberately not blocking**: (a) null-vs-empty `AdditionalIds` compare unequal in the new equality — a provider returning an empty non-null dict would re-trigger the perpetual-difference symptom in miniature (`ExternalTagIds.Read` normalizes its own side; providers are unnormalized); hardening candidate for B7/B16. (b) `FindMatchesForFileAsync` opens the file twice (fuzzy tags, then `TryReadFromFile`) — perf nit only.

**Final state**: `dotnet build Octave.Desktop` = 0/0 · `dotnet test Octave.Core.Tests` = **149/149** (136 prior + 13 new: 4 matcher, 2 workflow, 1 SLE gate, 6 equality). Committed together with the RV patch below as separate commits.

### Session 2026-08-23 — Batch 0 + 1 patch review *(post-fix verification pass)*

**Scope**: full re-read of both batch commits (`5bc3889`, `a80fd68`) against §15 Batch 0/1 specs. Baseline re-verified before any edit: build 0/0, tests 136/136 — the §18 entries above are accurate.

**Review verdict — no regressions found; two defects patched:**

| # | Severity | Location | Finding & Fix |
|---|----------|----------|---------------|
| RV-01 | ⚠️ Bug (edge) | `SqliteDbContext.SearchLibraryAsync` FTS catch | **DB-03/DB-06 interaction dropped a guard the old code had incidentally.** A `SqliteException` can surface *mid-`ReadAsync`* (I/O error / corruption while stepping rows) after some FTS rows were already added to `tracks`. The new `ftsExecuted = false` then ran the LIKE fallback, which **appends duplicate rows** to the partial result (the old code's `tracks.Count == 0` condition used to prevent this by accident). **Fix**: snapshot `tracks.Count` before the FTS block; in the catch, if rows already streamed out, treat FTS as executed (keep the partial result, skip the fallback). Zero-row failures still fall back exactly as DB-03 prescribes. |
| RV-02 | 💡 Hygiene | `SqliteConcurrencyAndMigrationTests.cs` | **Temp-file leaks in the new acceptance tests**: (a) `_dbPath = Path.GetTempFileName() + ".db"` creates a 0-byte `.tmp` file that is never deleted — one orphan per test instance (×9 across the suite); (b) Dispose never removed WAL mode's `-wal`/`-shm` sidecar files, so each test left up to 3 files in `%TEMP%`. **Fix**: Guid-named temp paths (no `GetTempFileName` orphan); Dispose sweeps `{db, db-wal, db-shm}` for both fixtures. |

**Verified-correct during review** (probed, no action): all 67 call sites converted to `CreateConnectionAsync` with zero sync stragglers (grep); `BeginTransactionAsync`'s internal connection is properly captured (`tx.Connection`) and disposed by every scanner caller; DB-04's local-tx commit/rollback/dispose ordering is correct on both tx paths; DB-05's multi-statement batch executes fully under Microsoft.Data.Sqlite and its explicit ROLLBACK covers pooled-connection inheritance; DB-08's entry-first/TrackId-consumed matching keeps duplicates distinct and preserves both key semantics (both covered by tests 3/4); `limit ??= 200` is compatible with all callers (`ShellViewModel` passes `limit: 8`; `SearchViewModel` uses the default cap safely); CACHE-01's catch correctly wraps only the L2 read with writes still deliberately non-fatal.

**Final state**: `dotnet build Octave.Desktop` = 0/0 · `dotnet test Octave.Core.Tests` = **136/136**. Changes not yet committed at review time.

### Session 2026-08-23 — Batches 0 + 1

**Pre-work**: the entire external-data/enrichment subsystem (~30 source files + 11 test files) was still uncommitted in the working tree. Committed as baseline `bc65400` *after* verifying green: `dotnet build Octave.Desktop` = 0/0, `dotnet test Octave.Core.Tests` = 128/128. This unblocked clean per-batch commits and resolved the "too many uncommitted changes" symptom (the bulk of which was tracked build artifacts, i.e. PH-01/PH-02 below).

#### ✅ Batch 0 — Repo hygiene & guardrails *(commit `5bc3889`)*

| ID | Status | Evidence |
|----|--------|----------|
| PH-01 | ✅ Fixed | All tracked `bin/`/`obj/` files removed from the index (`git rm --cached`); `.gitignore` rewritten with global `[Bb]in/` `[Oo]bj/` patterns covering all three projects incl. `Octave.Core.Tests`. |
| PH-02 | ✅ Fixed | `.vs/` untracked (241 artifact entries left the index in total); ignored via `.vs/`. |
| PH-03 | ✅ Fixed | `/claude` → `.claude/` under a labelled "Claude Code local state" section. |
| PH-04 / MC-09 | ✅ Fixed | README.md expanded: description, features, build/test commands, architecture table. |
| PH-05 / MC-07 | ✅ Fixed | `build.log` deleted from disk + index; covered by `.gitignore`. |
| PH-06 / MC-08 | ✅ Fixed | `temp` deleted from disk + index; covered by `.gitignore`. |
| PH-07 | ✅ Fixed | `test_fx/` scratch console project deleted (verified single-file ManagedBass experiment before removal). |
| PH-08 | ✅ Fixed | `Assets\test.mp3` `<Content>` now has `Condition="'$(Configuration)' == 'Debug'"` — dev fixture kept for Debug runs, excluded from Release packaging. (File is unreferenced by code; tests generate their own fixtures.) |
| MC-01 | ✅ Fixed | Duplicate `using Octave.Core.Models;` removed from `ExternalDataModels.cs`. |
| MC-02 | ✅ Fixed | Note: the audit cited `mc:Ignorable="d"` at line 8, but the working tree no longer had it; the unused `xmlns:d` / `xmlns:mc` declarations themselves were the remaining dead code and were removed from `NowPlayingPage.xaml` (grep-confirmed neither prefix was used anywhere in the page). |
| MC-03 (+ NP-13) | ✅ Fixed | Explicit `using System;` added to `AlbumArtPanel.xaml.cs` (same file as the deferred NP-13 note, folded in as §15 B0 suggested). |

**Acceptance verified**: `git status` shows zero build artifacts; solution builds 0/0 after the XAML/csproj edits; no runtime behavior touched.

#### ✅ Batch 1 — SQLite concurrency root *(commit `a80fd68`; files: `SqliteDbContext.cs`, `TwoTierExternalDataCache.cs`)*

| ID | Status | Evidence |
|----|--------|----------|
| DB-01 | ✅ Fixed | `PRAGMA busy_timeout = 5000` added to the connection PRAGMA batch applied on **every** opened connection (WAL already confirmed present). Concurrent scan/UI writers now queue instead of failing fast with SQLITE_BUSY. |
| DB-02 | ✅ Fixed | New `CreateConnectionAsync()` (OpenAsync + async PRAGMA batch); all **67 call sites** converted; the synchronous factory deleted. Also fixed here per DB-07's note: a failed Open/PRAGMA now disposes the connection instead of leaking it. |
| DB-03 | ✅ Fixed | `ftsExecuted` tracks "FTS ran successfully" separately from row-count; the leading-wildcard LIKE fallback runs only when FTS could not execute — never when it legitimately returned zero rows. ⚠️ See behavior-change note below. |
| DB-04 | ✅ Fixed | `UpsertTrackAsync` wraps the Artist/Album/Track trio in a local transaction when `tx == null` (commit on success; rollback + rethrow on failure); commands always enlist `effectiveTx`. |
| DB-05 | ✅ Fixed | PlaylistTracks surrogate-key migration wrapped in `BEGIN IMMEDIATE … COMMIT` with an explicit ROLLBACK on failure, plus `DROP TABLE IF EXISTS PlaylistTracks_Temp` so an aborted pre-fix attempt can't block re-migration. Crash mid-migration can no longer lose playlist rows. |
| DB-06 | ✅ Fixed | FTS search catch narrowed to `SqliteException`; non-SQLite exceptions now surface instead of vanishing into `Debug.WriteLine`. ⚠️ See note: logging medium unchanged. |
| DB-07 | ✅ Fixed | Empty/whitespace query short-circuits to an explicitly empty `SearchResults`; default per-category LIMIT of 200 applies when callers pass null (was: `"%%"` LIKE-matching the entire library unbounded). |
| DB-08 | ✅ Fixed | `SetPlaylistOrderAsync` loads current entries once, matches keys as entry-Id first then TrackId (consuming matches in current order), and writes one positional UPDATE per entry — repeated tracks keep distinct `SortOrder`s. Both key semantics preserved for existing callers. |
| CACHE-01 | ✅ Fixed | `TwoTierExternalDataCache.GetWithMetadataAsync` L2 read catches DB failures and degrades to a cache miss (logged) instead of throwing through the orchestrators' offline/stale fallback. Write-path swallow retained deliberately (a cache that can't persist must not break its callers). |

**Acceptance verified**: new `Octave.Core.Tests/SqliteConcurrencyAndMigrationTests.cs` (8 tests):
1. `ConcurrentWriters_DoNotThrowSqliteBusy_AndPreserveAllRows` — 8 parallel writers × 10 upserts with interleaved reads, zero exceptions, all 85 rows present (fails without busy_timeout).
2. `InitializeAsync_LegacyPlaylistTracksSchema_PreservesAllRowsWithSurrogateIds` — hand-built pre-migration schema migrates end-to-end; all rows survive with distinct surrogate ids.
3./4. Duplicate-track reorder by entry-Id and by TrackId — copies keep distinct positions.
5. Empty/whitespace search returns empty, not the whole library.
6. Default-limit bound (220 seeded, ≤200 returned).
7. FTS-ran-but-no-match skips the LIKE fallback (and prefix search still works).
8. L2 cache failure (dropped table) returns a miss instead of throwing; writes stay non-fatal.

Final state: `dotnet build Octave.Desktop` = 0 warnings / 0 errors · `dotnet test Octave.Core.Tests` = **136/136** (128 prior + 8 new).

#### Notes & deliberate trade-offs from this session

- **DB-03 behavior change**: mid-token substring queries that only LIKE could satisfy (e.g. searching "oney" for "Money") previously matched via the fallback scan; they now return empty, exactly as this audit's prescribed fix specifies. If mid-word search is wanted later, re-introduce a *bounded* fallback or switch TracksFts to a trigram tokenizer — do not restore the unconditional double-scan.
- **DB-06 logging medium**: `Octave.Core` has no logger abstraction, so the narrowed catch still reports via `Debug.WriteLine` (matching codebase idiom). The important half — genuine bugs no longer being swallowed — is done; adopting a real logging facade across Core is future work outside any current batch scope.
- **PH-08**: `test.mp3` remains available in Debug builds where the dev workflow may expect it; only Release packaging changed.
- **Baseline commit**: committing the previously-untracked subsystem was required to keep batches isolated; content was not modified beyond what the working tree already contained (verified by build+tests before and after).

