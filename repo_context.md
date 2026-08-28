# OCTAVE - Comprehensive Repository Context & Architectural Map

This document serves as the definitive technical reference for the **OCTAVE** codebase, mapping all architecture, subsystems, services, data models, UI layers, lifecycle patterns, invariants, and dependencies.

---

## 1. Solution & Architecture Overview

OCTAVE is a high-performance, high-fidelity music player for Windows built on **.NET 10**, **C# 13**, **WinUI 3 (Windows App SDK / Windows 10 build 26100+ unpackaged self-contained)**, **ManagedBass (BASS Audio Engine)**, **SQLite (`Microsoft.Data.Sqlite`)**, and **LRCLIB** online metadata integration.

```
OCTAVE/
├── Octave.Core/             # Core business logic, SQLite database, audio engine, scanner, metadata, LRCLIB
│   ├── Helpers/             # IdGenerator, ShellRecycleBin, Clamps, Normalizer, WindowsAudioDeviceHelper, ImageValidator, ImageDimensionReader
│   ├── Interfaces/          # ILibraryService, IAudioPlayerService, IQueueService, IPlaylistService, ILyricsService, ILrclibClient, ILyricsMatcher, ILyricsRepository, etc.
│   ├── Models/              # Track, Album, Artist, Playlist, QueueItem, PlaybackState, AudioQualityDetails, LyricLine, LyricsData, LrclibResponse
│   └── Services/
│       ├── Audio/           # ManagedBassAudioService (BASS engine, 10-band EQ, crossfade, DSP, FFT)
│       ├── Database/        # SqliteDbContext (SQLite tables, triggers, WAL, FTS5, schema migrations, ILyricsRepository)
│       ├── Library/         # LibraryService, LocalLibraryScanner, LibraryWatcherService
│       ├── Metadata/        # ArtworkCacheManager, LyricsService, LrclibClient, LyricsMatcher
│       └── Playback/        # QueueService, PlaylistService
├── Octave.Desktop/          # WinUI 3 Desktop Client Application (x64 Native Unpackaged Self-Contained)
│   ├── Controls/            # SpectrumVisualizerControl, NowPlaying panels (AlbumArt, Lyrics, Queue, Credits)
│   ├── Converters/          # ArtworkPathConverter, DbFormatConverter, DurationConverter, BoolConverters, GlyphConverters
│   ├── Helpers/             # TrackContextMenu, CollectionDiff, CrashLog, ThemeHelper, WindowHelper, WindowMinSizeHelper
│   ├── Services/            # WindowsSmtcService (System Media Transport Controls), WinUIDispatcherService
│   ├── ViewModels/          # ShellViewModel, Home, Library, Albums, Artists, NowPlaying, Playlists, Search, Settings, EqBand
│   └── Views/               # HomePage, LibraryPage, AlbumsPage, ArtistsPage, EntityDetailPage, PlaylistsPage, PlaylistDetailPage, SearchResultsPage, SettingsPage, NowPlayingPage, MiniPlayerWindow
└── Octave.Core.Tests/       # xUnit test suite (232 tests: Sqlite, Library, Audio, Queue, Lyrics, LRCLIB, Matcher, Scanner, Watcher)
```

---

## 2. Domain Models & Core Types

### 2.1 Domain Records (`DomainModels.cs`)
- **`Track`**:
  ```csharp
  public record Track(
      string Id,
      string Title,
      string ArtistId,
      string ArtistName,
      string AlbumId,
      string AlbumTitle,
      double DurationSeconds,
      string SourceUri,
      int TrackNumber,
      int Year,
      DateTime DateAdded,
      string Genre = "",
      float ReplayGain = 0.0f,
      int DiscNumber = 1
  );
  ```
- **`Album`**:
  ```csharp
  public record Album(
      string Id,
      string Title,
      string ArtistId,
      string ArtistName,
      int Year,
      string? ArtworkUrl
  );
  ```
- **`Artist`**:
  ```csharp
  public record Artist(
      string Id,
      string Name,
      string? Bio,
      string? ArtworkUrl
  );
  ```
- **`Playlist`**:
  ```csharp
  public record Playlist(
      string Id,
      string Title,
      string? Description,
      DateTime CreatedAt,
      int TrackCount = 0
  );
  ```
- **`PlaylistTrackEntry`**:
  ```csharp
  public record PlaylistTrackEntry(
      string Id, // Surrogate primary key for playlist entries
      string PlaylistId,
      Track Track,
      int SortOrder
  );
  ```
- **`QueueItem`**:
  ```csharp
  public class QueueItem : ObservableObject
  {
      public string Id { get; set; } = Guid.NewGuid().ToString();
      public Track Track { get; set; }
      public bool IsPlaying { get; set; }
      public string? ArtworkUrl { get; set; }
  }
  ```
- **`PlaybackState`**:
  ```csharp
  public record PlaybackState(
      Track? CurrentTrack,
      PlaybackStatus Status,
      double PositionSeconds,
      double DurationSeconds,
      float Volume,
      bool IsMuted,
      bool IsShuffle,
      RepeatMode RepeatMode,
      long SequenceToken = 0
  );
  ```
- **`AudioQualityDetails`**:
  ```csharp
  public record AudioQualityDetails(
      string Codec,
      int BitrateKbps,
      int SampleRateHz,
      int BitsPerSample,
      int Channels,
      string ChannelLayout,
      bool IsHiResLossless,
      bool IsLossless
  );
  ```

### 2.2 Lyrics Models (`LyricsModels.cs`)
- **`LyricLine`**: `TimeSpan Start`, `TimeSpan? End`, `string Text`.
- **`LyricsState`**: `Loading`, `Synced`, `Unsynced`, `Unavailable`, `NetworkUnavailable`, `Resolving`.
- **`LyricDisplayMode`**: `Synced`, `Static`.
- **`LyricsData`**: Holds track ID, state, synced lines, plain text, availability flags, raw LRC string, error flag, and source attributions (`SyncedSource`, `StaticSource`, `Source`).
- **`LrclibResponse`**: JSON response model for LRCLIB API (`id`, `name`, `trackName`, `artistName`, `albumName`, `duration`, `instrumental`, `plainLyrics`, `syncedLyrics`).

---

## 3. SQLite Database Layer (`SqliteDbContext.cs`)

### 3.1 Database Configuration & Pragmas
- **Storage Path**: `%LOCALAPPDATA%/Octave/octave.db` (native unpackaged).
- **Pragmas**:
  - `PRAGMA busy_timeout = 5000;`
  - `PRAGMA journal_mode = WAL;`
  - `PRAGMA foreign_keys = ON;`
  - `PRAGMA synchronous = NORMAL;`

### 3.2 Tables & Schema
1. **`Artists`**: `Id TEXT PRIMARY KEY`, `Name TEXT NOT NULL COLLATE NOCASE`, `Bio TEXT`, `ArtworkUrl TEXT`.
2. **`Albums`**: `Id TEXT PRIMARY KEY`, `Title TEXT NOT NULL COLLATE NOCASE`, `ArtistId TEXT NOT NULL REFERENCES Artists(Id) ON DELETE CASCADE`, `ArtistName TEXT NOT NULL`, `Year INTEGER`, `ArtworkUrl TEXT`.
3. **`Tracks`**: `Id TEXT PRIMARY KEY`, `Title TEXT NOT NULL COLLATE NOCASE`, `ArtistId TEXT NOT NULL REFERENCES Artists(Id) ON DELETE CASCADE`, `ArtistName TEXT NOT NULL`, `AlbumId TEXT NOT NULL REFERENCES Albums(Id) ON DELETE CASCADE`, `AlbumTitle TEXT NOT NULL`, `DurationSeconds REAL`, `SourceUri TEXT UNIQUE NOT NULL`, `TrackNumber INTEGER`, `Year INTEGER`, `DateAdded TEXT NOT NULL`, `Genre TEXT`, `ReplayGain REAL`, `Disc INTEGER`.
4. **`TracksFts`**: FTS5 virtual table (`Title`, `ArtistName`, `AlbumTitle`, `Genre`, `tokenize='unicode61'`), kept synchronized via `Tracks_ai`, `Tracks_ad`, `Tracks_au` triggers.
5. **`Playlists`**: `Id TEXT PRIMARY KEY`, `Title TEXT NOT NULL`, `Description TEXT`, `CreatedAt TEXT NOT NULL`.
6. **`PlaylistTracks`**: `Id TEXT PRIMARY KEY`, `PlaylistId TEXT NOT NULL REFERENCES Playlists(Id) ON DELETE CASCADE`, `TrackId TEXT NOT NULL REFERENCES Tracks(Id) ON DELETE CASCADE`, `SortOrder INTEGER NOT NULL`.
7. **`PlaybackHistory`**: `Id INTEGER PRIMARY KEY AUTOINCREMENT`, `TrackId TEXT NOT NULL REFERENCES Tracks(Id) ON DELETE CASCADE`, `PlayedAt TEXT NOT NULL`.
8. **`Favorites`**: `TrackId TEXT PRIMARY KEY REFERENCES Tracks(Id) ON DELETE CASCADE`, `AddedAt TEXT NOT NULL`.
9. **`PlayerState`**: `Id INTEGER PRIMARY KEY CHECK (Id = 1)`, `CurrentIndex INTEGER`, `PositionSeconds REAL`, `Volume REAL`, `IsShuffle INTEGER`, `RepeatMode INTEGER`, `UpdatedAt TEXT`.
10. **`SavedQueue`** & **`SavedUnshuffledQueue`**: `SortOrder INTEGER PRIMARY KEY`, `TrackId TEXT NOT NULL REFERENCES Tracks(Id) ON DELETE CASCADE`.
11. **`MonitoredFolders`**: `Path TEXT PRIMARY KEY`.
12. **`LyricsCache`**: `TrackId TEXT PRIMARY KEY REFERENCES Tracks(Id) ON DELETE CASCADE`, `PlainLyrics TEXT`, `SyncedLyrics TEXT`, `HasPlainLyrics INTEGER NOT NULL`, `HasSyncedLyrics INTEGER NOT NULL`, `IsNotFound INTEGER NOT NULL`, `LastCheckedAt TEXT NOT NULL`, `Source TEXT`, `LrclibRecordId INTEGER`, `SyncedSource TEXT`, `StaticSource TEXT`.
13. **`AppSettings`**: `Key TEXT PRIMARY KEY`, `Value TEXT NOT NULL`.
14. **`SchemaVersion`**: `Version INTEGER PRIMARY KEY`, `AppliedAt TEXT NOT NULL`.

---

## 4. Audio Playback Engine (`ManagedBassAudioService.cs` & `IAudioPlayerService.cs`)

### 4.1 Native BASS Subsystem
- **Core Library**: `bass.dll` (handles MP3, MP2, MP1, OGG, WAV, AIFF natively).
- **Decoder Plugins**: `bassflac.dll`, `bassopus.dll`, `bass_aac.dll`, `bassalac.dll`, `basswma.dll`, `bassdsd.dll`, `bassape.dll`, `basswv.dll`. Note: `bass_fx.dll` is an effect library dynamically linked via `ManagedBass.Fx`, not a plugin decoder.
- **Dynamic Device Tracking**: Uses `Bass.Configure(Configuration.IncludeDefaultDevice, true)` and `Bass.Configure(Configuration.DevNonStop, true)` to seamlessly follow Windows audio device changes.
- **Resilient Fallback**: Automatically falls back to No Sound device (`0`) if no hardware device is available, with self-recovery on subsequent `Play()` calls.

### 4.2 Stream Playback & Synchronization Invariants
- **End-of-Track Synchronization**: Uses standard `SyncFlags.End` (without `SyncFlags.Mixtime`) to ensure notifications trigger after audible buffer playback completes without holding BASS internal mixer locks, avoiding deadlocks.
- **Per-Stream Identity**: `_endSyncs` maps stream handles to `(SyncHandle, SessionId, Uri)`. Old/crossfaded streams have their sync detached at skip time so late endings cannot trigger false auto-advances.
- **Crossfading**: Configurable volume ramp via `Bass.ChannelSlideAttribute` (up to 10 seconds).
- **10-Band Graphic Equalizer**: Implemented via `BASS_FX_DX8_PARAMEQ` (31Hz, 62Hz, 125Hz, 250Hz, 500Hz, 1kHz, 2kHz, 4kHz, 8kHz, 16kHz) with automatic volume clamping and peak limiter.
- **FFT Spectrum Visualizer**: Fast, real-time frequency analysis via `Bass.ChannelGetData(..., (int)DataFlags.FFT2048)`.
- **Thread Safety**:
  - `_playGate` serializes concurrent `Play` calls.
  - `_streamLock` guards stream handle mutations (never held across slow I/O or event dispatches).
  - All public events (`TrackStarted`, `TrackEnded`, `PositionChanged`) are dispatched outside `_streamLock`.

---

## 5. Playback Queue & Playlist Management (`QueueService.cs` & `PlaylistService.cs`)

### 5.1 `QueueService`
- **Active & Unshuffled Queues**: Maintains an active playback queue (`ObservableCollection<QueueItem>`) and an unshuffled backup to preserve the original album/playlist order when toggling Shuffle off.
- **Shuffle**: Implements Fisher-Yates shuffle with the currently playing track pinned at index 0. Injectable `Random` instance allows deterministic testing.
- **Repeat Modes**: `None` (stops at end of queue), `Queue` (loops queue), `Track` (repeats current track).
- **Auto-Advance & Resilient Error Handling**:
  - Distinguishes natural track completion (`IsNaturalEnd == true`) from stream load failures (`IsNaturalEnd == false`).
  - Natural completion resets `_consecutiveLoadFailures = 0` and advances to next track.
  - Undecodable files trip circuit breaker after `Math.Min(_activeQueue.Count, 10)` consecutive failures.
  - Database history logging (`LogPlaybackHistoryAsync`) is protected with `try...catch` so database I/O issues never abort track transitions.
- **Player State Persistence**: Persists playback state, queue, and position to SQLite using throttled lightweight writes (every 5 seconds) and atomic full-state snapshots.

### 5.2 `PlaylistService`
- Complete CRUD operations for playlists and playlist track entries with unique surrogate primary keys (`PlaylistTracks.Id`), allowing duplicate tracks, drag-and-drop reordering, and track removal.

---

## 6. Local Music Library Harvester (`LocalLibraryScanner.cs`, `LibraryWatcherService.cs`, `LibraryService.cs`)

### 6.1 `LocalLibraryScanner`
- Asynchronous producer-consumer channel (`Channel.CreateBounded<PreparedTrack>`) traversing directories and extracting tags using `TagLib#`.
- Reads ID3v1, ID3v2, Vorbis comments, MP4 tags, APE tags.
- Extracts embedded album art and caches it to disk (`ArtworkCache/{hash}.jpg`).
- Falls back to folder images (`folder.jpg`, `cover.jpg`, `album.jpg`).
- Batch writes tracks into SQLite in transactions of 200 items.
- Automatically prunes missing/deleted files during folder reconciliation.

### 6.2 `LibraryWatcherService`
- Multi-folder `FileSystemWatcher` service monitoring user-configured directories in real-time.
- Debounces file system create/modify/delete/rename events with a 2-second sliding window.
- Retries locked files safely before re-indexing.
- Periodically attempts to reconnect disconnected storage roots (e.g. USB drives).

### 6.3 `LibraryService`
- Unified facade exposing query and mutation APIs for tracks, albums, artists, favorites, playlists, monitored folders, duplicate detection, search, and library database reset.
- Broadcasts `LibraryUpdated` and `FavoritesChanged` application-wide.

---

## 7. Metadata, Artwork & Hybrid Lyrics Pipeline

### 7.1 `ArtworkCacheManager` (`ArtworkCacheManager.cs`)
- Computes SHA-256 hashes of extracted album art binaries and saves them to `<LocalState>/ArtworkCache/{hash}.jpg`.
- Maintains an in-memory existence probe cache (`_existenceCache`) with 30s TTL to prevent repeated file system calls.
- Supports full disk cache purge via `ClearCacheAsync()`.

### 7.2 `LyricsService` & LRCLIB Integration (`LyricsService.cs`, `LrclibClient.cs`, `LyricsMatcher.cs`)
- **Two-Phase Lyrics Pipeline & Ultra-Low Latency Caching**:
  - **L1 In-Memory Cache**: `ConcurrentDictionary<string, LyricsData>` provides sub-millisecond (< 0.1ms) instant delivery for repeated and pre-warmed tracks.
  - **Phase 1 (Database-First, < 1ms)**: Checks L1 in-memory cache -> SQLite `LyricsCache` (primary key indexed). If complete or negative-cached, returns immediately without touching disk files. Only falls back to local embedded audio tags (`Tag.Lyrics`) and sidecar `.lrc` files (`{SongName}.lrc`, `./Lyrics/{SongName}.lrc`) if SQLite cache is missing/incomplete.
  - **Phase 2 (Background Enrichment & Non-Blocking Persistence)**: Queries LRCLIB (`https://lrclib.net` via `LrclibClient`) using two-stage lookup:
    1. Direct `/api/get` lookup by `(track_name, artist_name, album_name, duration)`.
    2. Fallback fuzzy search `/api/search?q={title}+{artist}` + intelligent scoring via `LyricsMatcher` (Title Dice coefficient, Artist set overlap, duration delta, album correlation).
    3. Persists to SQLite in non-blocking background tasks without delaying UI rendering.
  - **Proactive Up-Next Pre-warming**: `NowPlayingViewModel` proactively pre-warms the lyrics cache for the next track in the queue when the queue changes or playback reaches $\ge 75\%$ duration, enabling perceived 0ms lyrics loading on track transition.
- **LRC Parsing**: Robust timestamp parsing supporting 1-, 2-, and 3-digit fractional milliseconds (`[mm:ss.xx]`) and global offset tags (`[offset:+-ms]`).
- **Negative Caching**: 7-day negative cache for tracks verified unavailable on LRCLIB to prevent redundant network lookups.

---

---

## 8. Desktop UI Layer (WinUI 3 & MVVM)

### 8.1 Views & Navigation
- **`MainWindow.xaml`**: Mica backdrop, unified single title bar (`ExtendsContentIntoTitleBar = true` with transparent caption buttons), top navigation bar, global search box with live suggestions dropdown, fluid `NavigationThemeTransition` (`EntranceNavigationTransitionInfo`), persistent bottom playback bar with dedicated Lyrics toggle, and a **Docked Inline Right Sidebar (`DisplayMode="Inline"`)**:
  - **Dynamic Mica Window Backdrop**: Right sidebar pane uses a transparent container directly sitting over the multi-layered dynamic album art / Mica tint, matching the left sidebar and window title bar.
  - **Symmetrical Middle App Island**: Overrode `NavigationViewContentGridCornerRadius` to `8,8,0,0` with right margin `6px`, ensuring the middle content app has smooth rounded curves on both its top-left and top-right corners.
  - **Modern Segmented Pill Navigation**: Custom-styled segmented tab buttons ("Info", "Up Next", "Lyrics") with direct spacing beneath to the view body and a dedicated top-right **"✕"** close button.
  - **Glitch-Free Navigation Auto-Collapse**: Automatically collapses the left `NavigationView` pane into icon-only mode when the right sidebar opens and safely preserves user manual pane toggles without split-second jitter upon sidebar close.
  - **Overhauled Info Tab**: Distinguishes "About Track" and "Audio & File Details" within rounded elevated cards, dynamically hiding any missing metadata entries.
  - **Up Next Tab (Overhauled with Drag-to-Reorder)**: Elevated card container with dynamic track count in header (`UP NEXT • X tracks`), clean empty state placeholder, rich track rows with 36x36 artwork thumbnails, live active playing highlight tint, duration text, favorite toggle, play indicator, drag-and-drop reordering seamlessly synced with `IQueueService.Reorder`, and item removal.
  - **Lyrics Tab**: Real-time synchronized lyrics panel with ±500ms sync offset nudges (with conditional reset icon appearing only when offset != 0).
  - **NowPlayingPage Integration**: Navigating to `NowPlayingPage` automatically collapses the right sidebar, disables/greys out bottom bar sidebar trigger buttons, and pauses the left track title trigger to prevent UI competition. Overhauled `QueuePanel` with matching `CardBackgroundFillColorDefaultBrush`, `CornerRadius="8"`, drag-to-reorder, and empty state handling.
- **`HomePage.xaml`**: Dashboard featuring Recent tracks, Favorites, Recently Added, Most Played, and quick playback actions with responsive card hover elevations.
- **`LibraryPage.xaml`**: Full sortable track grid with column headers, instant search/filter, full ThemeResource integration, 3-dot context menu, and **Multi-Select Mode** (Play Selected, Add to Queue, Add to Playlist with dynamic flyouts and new playlist dialog creator, Select All, and Done).
- **`AlbumsPage.xaml` & `ArtistsPage.xaml`**: Responsive grid tiles with album art, artist imagery, track counts, card `PointerOver` visual state outlines, and seamless navigation to `EntityDetailPage`.
- **`EntityDetailPage.xaml`**: Hero header banner with large artwork, title, subtitle, "Play All" button, and track list.
- **`PlaylistsPage.xaml` & `PlaylistDetailPage.xaml`**: Playlist browsing, track management, reordering, creation, and deletion with:
  - **"Add Songs" Flow**: Search-enabled library track picker `ContentDialog` supporting bulk song insertion into the playlist.
  - **In-Playlist Multi-Select**: Bulk track removal, queueing, and playback.
- **`SearchResultsPage.xaml`**: Uniform layout margins, sectioned category results (Tracks, Albums, Artists, Playlists), and rich elevated empty search state card.
- **`NowPlayingPage.xaml`**: Full-window experience with `NavigationCacheMode="Required"` for instantaneous zero-latency navigation, featuring 4 interchangeable panels:
  1. `AlbumArtPanel`: High-resolution album artwork with a translucent badge favorite toggle heart button in the bottom-left corner.
  2. `LyricsPanel`: Real-time synchronized scrolling lyrics with active line highlighting, ±500ms sync offset nudges (with conditional reset icon appearing only when offset != 0), auto-follow suspension on scroll, and streamlined toolbar.
  3. `QueuePanel`: Interactive queue management with drag-to-reorder, favorite toggle, remove, and play actions.
  4. `CreditsPanel`: Technical audio stream metadata across 4 rich elevated tiles (Artist Profile, Album Overview, Audio Stream Specifications, and Track & Tag Properties: Genre, Track/Disc #, ReplayGain, Bit Depth/Channels, Location) with `CardBackgroundFillColorDefaultBrush` and `CornerRadius="8"`.
- **`SettingsPage.xaml`**:
  - **Playback**: Sleep timer, Session resume persistence, Waveform visualizer toggle, Crossfade duration (1-10s) with atomic SQLite `AppSettings` persistence.
  - **Equalizer**: 10-band interactive sliders, DSP status, preset gain curves (Flat, Bass Boost, Treble Boost, Vocal, Electronic, Acoustic), and persisted EQ gains array.
  - **Music Library**: Monitored folders management (Add Folder, Rescan All, Remove Folder), Duplicate Tracks finder, and Clear Library Database & Cache button.
  - **Engine Diagnostics**: Audio engine self-test static fire harness with live terminal output.
- **`MiniPlayerWindow.xaml`**: Compact, always-on-top floating player with `MicaBackdrop`, artwork, track info, progress slider, and playback controls.

### 8.2 Converters, Helpers & Favorites System
- **`FavoriteConverters`**: `FavoriteGlyphConverter` (`\uEB52` HeartFill / `\uEB51` HeartOutline), `FavoriteBrushConverter` (coral red `#FF4060` / theme-aware tertiary brush), and `FavoriteToolTipConverter` ("Remove from Favorites" / "Add to Favorites").
- **Track Lists Favorite Buttons**: Available on every page listing songs (`LibraryPage`, `EntityDetailPage`, `PlaylistDetailPage`, `SearchResultsPage`, `QueuePanel`, `MainWindow` sidebar queue, and `HomePage` Spotlight Hero card) with live synchronization via `ILibraryService.FavoritesChanged`.
- **`WindowsAudioDeviceHelper`**: Fast, cached audio endpoint query engine with 60-second positive TTL and 5-minute failure fallback caching to eliminate thread-pool COM activation thrashing and lag on page navigation.
- **`WindowsSmtcService`**: Windows System Media Transport Controls integration supporting hardware media keys (Play, Pause, Next, Previous) and lock screen / flyout metadata.

---

## 9. Architectural Invariants & Thread Safety

1. **Lock Ordering & Hierarchy**:
   - `_queueLock` in `QueueService` -> `_playGate` in `ManagedBassAudioService` -> `_streamLock`.
   - Never acquire `_streamLock` before acquiring `_queueLock`.
   - Never raise public events (`PlaybackStateChanged`, `TrackStarted`, `TrackEnded`, `PositionChanged`) while holding locks.
2. **UI Dispatcher Boundary**:
   - Background tasks, BASS sync callbacks, and timer ticks must marshal UI property mutations and collection updates to the UI thread using `DispatcherQueue.TryEnqueue`.
   - `QueueItem` (the only `INotifyPropertyChanged` model in `Octave.Core`) routes property change notifications through `QueueItem.SetUIDispatcher` and safely catches WinRT `0x8001010E (RPC_E_WRONG_THREAD)` COM exceptions during background auto-advances.
3. **Native Callback Safety**:
   - Native BASS sync procedures (`OnTrackEndedCallback`) must never throw unhandled exceptions across the P/Invoke boundary and must dispatch work onto the `ThreadPool`.
4. **WinUI 3 ListView SelectionMode Safety**:
   - When switching a WinUI 3 `ListView` from `SelectionMode.Multiple` to `SelectionMode.None`, WinUI internally tears down its selection vector. Calling `SelectedItems.Clear()` after changing `SelectionMode` or on an unselected/empty collection throws WinRT COMException `0x8000FFFF (E_UNEXPECTED)`. Selection clearing must always precede mode changes and be guarded safely.
5. **WinUI 3 RangeBase (Slider) Value Clamping & Non-Zero Range Safety**:
   - In WinUI 3, assigning `RangeBase.Value > RangeBase.Maximum` or assigning `Value` when `Maximum == Minimum == 0` causes native C++ division by zero in `RangeBase::put_Value` (`normalized = (val - min) / (max - min)`), throwing unhandled `COMException 0x8000FFFF (E_UNEXPECTED)`. Slider maximums must bind to safe non-zero view model properties (`SliderMaximum => DurationSeconds > 0 ? DurationSeconds : 1.0`) and `IsEnabled => DurationSeconds > 0`, while `PositionSeconds` strictly clamps to 0 when `DurationSeconds <= 0`.

---

## 10. Test Suite Matrix (`Octave.Core.Tests`)

The test suite contains **233 unit and integration tests** covering all core layers:
- **`SqliteDbContextTests` & `SqliteConcurrencyAndMigrationTests`**: Schema creation, CRUD, FTS5 search triggers, cascade deletes, concurrent connections, WAL durability, bulk track insertion transactionality, and automatic dropping of obsolete legacy columns.
- **`ManagedBassAudioServiceTests`**: Real unmanaged BASS engine initialization, WAV stream decoding, crossfade volume ramping, End-sync registration, skip/detach safety, and idempotent disposal.
- **`QueueServiceTests` & `QueueItemTests`**: Queue operations, Fisher-Yates shuffle permutations, repeat modes, multi-track auto-advance sequences, load failure circuit breakers, cross-thread WinRT COM exception resilience, and state persistence.
- **`PlaylistServiceTests`**: Playlist CRUD, surrogate key uniqueness, duplicate track handling, track reordering, and atomic multi-track addition (`AddTracksAsync`).
- **`LibraryServiceTests` & `LocalLibraryScannerTests`**: Folder scanning, batch writes, tag reading, artwork caching, compilation artist separation, and duplicate detection.
- **`LyricsServiceTests`**: Embedded, sidecar `.lrc`, SQLite cache, L1 in-memory caching, and LRCLIB resolution pipelines, timestamp parsing, negative caching, and offset calculations.
- **`LrclibClientTests` & `LyricsMatcherTests`**: HTTP request generation, response parsing, error handling, title normalizations, and candidate fuzzy matching algorithms.
- **`MetadataTextNormalizerTests`, `AudioFormatAndDimensionTests` & `FormattingTests`**: 23 canonical audio formats, PNG/JPEG dimension parsing without GDI+, diacritic stripping, version matching, Levenshtein distances, and duration formatting.

