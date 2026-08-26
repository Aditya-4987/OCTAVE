# OCTAVE - Comprehensive Repository Context & Architectural Map

This document serves as the definitive technical reference for the **OCTAVE** codebase, mapping all architecture, subsystems, services, data models, UI layers, lifecycle patterns, invariants, and dependencies.

---

## 1. Solution & Architecture Overview

OCTAVE is a high-performance, high-fidelity, 100% offline local music player for Windows built on **.NET 10**, **C# 13**, **WinUI 3 (Windows App SDK)**, **ManagedBass (BASS Audio Engine)**, and **SQLite (`Microsoft.Data.Sqlite`)**.

```
OCTAVE/
├── Octave.Core/             # Core business logic, SQLite database, audio engine, scanner, metadata
│   ├── Helpers/             # IdGenerator, ShellRecycleBin, Clamps, Normalizer, WindowsAudioDeviceHelper
│   ├── Interfaces/          # ILibraryService, IAudioPlayerService, IQueueService, IPlaylistService, etc.
│   ├── Models/              # Track, Album, Artist, Playlist, QueueItem, PlaybackState, AudioQualityDetails
│   └── Services/
│       ├── Audio/           # ManagedBassAudioService (BASS engine, 10-band EQ, crossfade, DSP, FFT)
│       ├── Database/        # SqliteDbContext (SQLite tables, triggers, WAL, FTS5, schema migrations)
│       ├── Library/         # LibraryService, LocalLibraryScanner, LibraryWatcherService
│       ├── Metadata/        # ArtworkCacheManager, LyricsService
│       └── Playback/        # QueueService, PlaylistService
├── Octave.Desktop/          # WinUI 3 Desktop Client Application
│   ├── Controls/            # Visualizer, NowPlaying panels (Art, Lyrics, Queue, Credits)
│   ├── Converters/          # ArtworkPathConverter, DbFormatConverter, DurationConverter
│   ├── Helpers/             # TrackContextMenu, CollectionDiff, CrashLog, ThemeHelper
│   ├── Services/            # WindowsSmtcService (System Media Transport Controls)
│   ├── ViewModels/          # ShellViewModel, Home, Library, Albums, Artists, NowPlaying, Settings, etc.
│   └── Views/               # HomePage, LibraryPage, AlbumsPage, EntityDetailPage, SettingsPage, MiniPlayer
└── Octave.Core.Tests/       # xUnit test suite (SqliteDbContext, LibraryService, Audio, Queue, Lyrics, etc.)
```

---

## 2. Domain Models & Core Types (`DomainModels.cs`)

All models represent pure local library entities:

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
  public class QueueItem
  {
      public string Id { get; set; } = Guid.NewGuid().ToString();
      public Track Track { get; set; }
      public bool IsPlaying { get; set; }
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
- **`TrackEndedEventArgs`**:
  ```csharp
  public class TrackEndedEventArgs : EventArgs
  {
      public long SessionId { get; }
      public string SourceUri { get; }
      public bool IsNaturalEnd { get; } // true for natural completion, false for stream load failure
  }
  ```

---

## 3. SQLite Database Layer (`SqliteDbContext.cs`)

### 3.1 Database Configuration & Pragmas
- **Storage Path**: `<LocalAppData>/Packages/.../LocalState/octave.db` (packaged) or `%LOCALAPPDATA%/Octave/octave.db` (unpackaged).
- **Pragmas**:
  - `PRAGMA busy_timeout = 5000;`
  - `PRAGMA journal_mode = WAL;`
  - `PRAGMA foreign_keys = ON;`
  - `PRAGMA synchronous = NORMAL;`

### 3.2 Tables & Schema
1. **`Artists`**: `Id TEXT PRIMARY KEY`, `Name TEXT NOT NULL COLLATE NOCASE`, `Bio TEXT`, `ArtworkUrl TEXT`.
2. **`Albums`**: `Id TEXT PRIMARY KEY`, `Title TEXT NOT NULL COLLATE NOCASE`, `ArtistId TEXT NOT NULL REFERENCES Artists(Id) ON DELETE CASCADE`, `ArtistName TEXT NOT NULL`, `Year INTEGER`, `ArtworkUrl TEXT`.
3. **`Tracks`**: `Id TEXT PRIMARY KEY`, `Title TEXT NOT NULL COLLATE NOCASE`, `ArtistId TEXT NOT NULL REFERENCES Artists(Id) ON DELETE CASCADE`, `ArtistName TEXT NOT NULL`, `AlbumId TEXT NOT NULL REFERENCES Albums(Id) ON DELETE CASCADE`, `AlbumTitle TEXT NOT NULL`, `DurationSeconds REAL`, `SourceUri TEXT UNIQUE NOT NULL`, `TrackNumber INTEGER`, `Year INTEGER`, `DateAdded TEXT NOT NULL`, `Genre TEXT`, `ReplayGain REAL`, `Disc INTEGER`.
4. **`TracksFts`**: FTS5 virtual table (`Title`, `ArtistName`, `AlbumTitle`, `Genre`, `tokenize='unicode61'`), kept updated via `Tracks_ai`, `Tracks_ad`, `Tracks_au` triggers.
5. **`Playlists`**: `Id TEXT PRIMARY KEY`, `Title TEXT NOT NULL`, `Description TEXT`, `CreatedAt TEXT NOT NULL`.
6. **`PlaylistTracks`**: `Id TEXT PRIMARY KEY`, `PlaylistId TEXT NOT NULL REFERENCES Playlists(Id) ON DELETE CASCADE`, `TrackId TEXT NOT NULL REFERENCES Tracks(Id) ON DELETE CASCADE`, `SortOrder INTEGER NOT NULL`.
7. **`PlaybackHistory`**: `Id INTEGER PRIMARY KEY AUTOINCREMENT`, `TrackId TEXT NOT NULL REFERENCES Tracks(Id) ON DELETE CASCADE`, `PlayedAt TEXT NOT NULL`.
8. **`Favorites`**: `TrackId TEXT PRIMARY KEY REFERENCES Tracks(Id) ON DELETE CASCADE`, `AddedAt TEXT NOT NULL`.
9. **`PlayerState`**: `Id INTEGER PRIMARY KEY CHECK (Id = 1)`, `CurrentIndex INTEGER`, `PositionSeconds REAL`, `Volume REAL`, `IsShuffle INTEGER`, `RepeatMode INTEGER`, `UpdatedAt TEXT`.
10. **`SavedQueue`** & **`SavedUnshuffledQueue`**: `SortOrder INTEGER PRIMARY KEY`, `TrackId TEXT NOT NULL REFERENCES Tracks(Id) ON DELETE CASCADE`.
11. **`MonitoredFolders`**: `Path TEXT PRIMARY KEY`.
12. **`AppSettings`**: `Key TEXT PRIMARY KEY`, `Value TEXT NOT NULL`.
13. **`SchemaVersion`**: `Version INTEGER PRIMARY KEY`, `AppliedAt TEXT NOT NULL`.

### 3.3 Automatic Migration & Backward Compatibility
- In `InitializeAsync`, `TryDropColumnAsync` automatically drops legacy columns (`Artists.IsLocal`, `Albums.Provider`, `Tracks.Provider`, `Playlists.IsLocalOnly`) and drops obsolete external enrichment tables (`ExternalDataCache`, `LibraryEnrichmentState`, `LibraryEnrichmentSessions`) from previous database versions.

### 3.4 Full Database Reset (`ClearDatabaseAsync`)
- Clears library data and resets auto-increment sequences within a fast, transactional operation while preserving user configuration (`MonitoredFolders`, `AppSettings`, `SchemaVersion`).

---

## 4. Audio Playback Engine (`ManagedBassAudioService.cs` & `IAudioPlayerService.cs`)

### 4.1 Native BASS Subsystem
- **Core Library**: `bass.dll` (handles MP3, MP2, MP1, OGG, WAV, AIFF natively).
- **Loaded Plugins**: `bass_fx.dll`, `bassflac.dll`, `bassopus.dll`, `bass_aac.dll`, `bassalac.dll`, `basswma.dll`, `bassdsd.dll`, `bass_ape.dll`.
- **Dynamic Device Tracking**: Uses `Bass.Configure(Configuration.IncludeDefaultDevice, true)` and `Bass.Configure(Configuration.DevNonStop, true)` to seamlessly follow Windows audio device changes.
- **Silent Fallback**: Automatically falls back to No Sound device (`0`) if no hardware device is available, with safe self-recovery on subsequent `Play()` calls.

### 4.2 Stream Playback & Synchronization Invariants
- **End-of-Track Synchronization**: Uses standard `SyncFlags.End` (without `SyncFlags.Mixtime`). This ensures notifications trigger after audible buffer playback completes without holding BASS internal mixer locks, avoiding lock-inversion deadlocks.
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

## 7. Metadata & Local Lyrics (`ArtworkCacheManager.cs`, `LyricsService.cs`)

### 7.1 `ArtworkCacheManager`
- Computes SHA-256 hashes of extracted album art binaries and saves them to `<LocalState>/ArtworkCache/{hash}.jpg`.
- Maintains an in-memory existence probe cache (`_existenceCache`) to prevent repeated file system calls.
- Supports full disk cache purge via `ClearCacheAsync()`.

### 7.2 `LyricsService`
- Pure local lyrics engine:
  1. Inspects embedded audio tags (`Tag.Lyrics`).
  2. Searches for local sidecar `.lrc` files (`{SongName}.lrc`, `{SongName}.txt`, etc.).
- Robust timestamp parsing supporting 1-, 2-, and 3-digit fractional millisecond timestamps (`[mm:ss.xx]`) and global offset tags (`[offset:+-ms]`).

---

## 8. Desktop UI Layer (WinUI 3 & MVVM)

### 8.1 Views & Navigation
- **`MainWindow.xaml`**: Mica backdrop, custom title bar, top navigation bar, global search box with live suggestions dropdown, and persistent bottom playback bar.
- **`HomePage.xaml`**: Dashboard featuring Recent tracks, Favorites, Recently Added, Most Played, and quick playback actions.
- **`LibraryPage.xaml`**: Full sortable track grid with column headers (Title, Artist, Album, Duration, Year, Date Added), instant search/filter, and 3-dot context menu.
- **`AlbumsPage.xaml` & `ArtistsPage.xaml`**: Responsive grid tiles with album art, artist imagery, track counts, and seamless navigation to `EntityDetailPage`.
- **`EntityDetailPage.xaml`**: Hero header banner with large artwork, title, subtitle, "Play All" button, and track list.
- **`PlaylistsPage.xaml` & `PlaylistDetailPage.xaml`**: Playlist browsing, track management, reordering, creation, and deletion.
- **`NowPlayingPage.xaml`**: Full-window experience with 4 interchangeable panels:
  1. `AlbumArtPanel`: High-resolution album artwork.
  2. `LyricsPanel`: Real-time synchronized scrolling lyrics with active line highlighting.
  3. `QueuePanel`: Interactive queue management with reordering, remove, and play actions.
  4. `CreditsPanel`: Technical audio stream metadata (bitrate, sample rate, bit depth, channels, file size, codec, location).
- **`SettingsPage.xaml`**:
  - **Playback**: Sleep timer, Session resume, Waveform visualizer, Crossfade duration (0-10s).
  - **Equalizer**: 10-band interactive sliders, DSP status, and built-in presets (Flat, Bass Boost, Treble Boost, Vocal, Electronic, Acoustic).
  - **Music Library**: Monitored folders management (Add Folder, Rescan All, Remove Folder), Duplicate Tracks finder, and Clear Library Database & Cache button.
  - **Engine Diagnostics**: Audio engine self-test static fire harness with live terminal output.
- **`MiniPlayerWindow.xaml`**: Compact, always-on-top floating player with artwork, track info, progress slider, and playback controls.

### 8.2 Converters & Helpers
- **`ArtworkPathConverter`**: High-performance, DPI-aware `IValueConverter` for WinUI 3 `Image.Source` with LRU caching (250 items) and weak-reference scale listeners.
- **`WindowsSmtcService`**: Windows System Media Transport Controls integration supporting hardware media keys (Play, Pause, Next, Previous) and lock screen / flyout metadata.

---

## 9. Architectural Invariants & Thread Safety

1. **Pure Offline Operation**: Zero network calls, zero external web APIs, zero remote dependencies. All audio, metadata, art, and lyrics resolve locally from disk.
2. **Lock Ordering & Hierarchy**:
   - `_queueLock` in `QueueService` -> `_playGate` in `ManagedBassAudioService` -> `_streamLock`.
   - Never acquire `_streamLock` before acquiring `_queueLock`.
   - Never raise public events (`PlaybackStateChanged`, `TrackStarted`, `TrackEnded`, `PositionChanged`) while holding locks.
3. **UI Dispatcher Boundary**:
   - Background tasks, BASS sync callbacks, and timer ticks must marshal UI property mutations and collection updates to the UI thread using `DispatcherQueue.TryEnqueue`.
4. **Native Callback Safety**:
   - Native BASS sync procedures (`OnTrackEndedCallback`) must never throw unhandled exceptions across the P/Invoke boundary and must dispatch work onto the `ThreadPool`.

---

## 10. Test Suite Matrix (`Octave.Core.Tests`)

The test suite contains **163 unit tests** covering all core layers:
- **`SqliteDbContextTests` & `SqliteConcurrencyAndMigrationTests`**: Schema creation, CRUD, FTS5 search triggers, cascade deletes, concurrent connections, WAL durability, and automatic dropping of obsolete legacy columns.
- **`ManagedBassAudioServiceTests`**: Real unmanaged BASS engine initialization, WAV stream decoding, crossfade volume ramping, End-sync registration, skip/detach safety, and idempotent disposal.
- **`QueueServiceTests`**: Queue operations, Fisher-Yates shuffle permutations, repeat modes, multi-track auto-advance sequences, load failure circuit breakers, and state persistence.
- **`PlaylistServiceTests`**: Playlist CRUD, surrogate key uniqueness, duplicate track handling, and track reordering.
- **`LibraryServiceTests` & `LocalLibraryScannerTests`**: Folder scanning, batch writes, tag reading, artwork caching, and duplicate detection.
- **`LyricsServiceTests`**: Embedded and sidecar `.lrc` timestamp parsing, millisecond normalization, and offset calculation.
- **`ArtworkCacheManagerTests`**: SHA-256 hash generation, probe caching, and cache clearing.
