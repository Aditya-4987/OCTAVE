# OCTAVE - Standalone Portable Distribution Packaging & Architecture

## 1. Overview & Distribution Model

OCTAVE is distributed as a **self-contained, standalone x64 portable package** housed in the `Octave_Package/` folder. This deployment model fulfills all requirements for zero-installation, portable, and relocatable execution across Windows 10 and 11 (build 17763 to 26100+).

```
Octave_Package/
├── Octave.Desktop.exe               # Main WinUI 3 64-bit AMD64 application executable
├── Octave.Desktop.dll               # Application managed presentation logic
├── Octave.Desktop.pri               # Compiled WinUI 3 MRM resource indexing file
├── resources.pri                    # Canonical fallback resource index
├── Octave.Core.dll                  # Core business logic, SQLite engine, BASS bindings
│
├── App.xbf                          # Root application XAML binary
├── MainWindow.xbf                   # Main Shell window XAML binary
├── MiniPlayerWindow.xbf             # Compact floating player window XAML binary
│
├── Views/                           # Compiled WinUI 3 Page XAML Binaries
│   ├── HomePage.xbf
│   ├── LibraryPage.xbf
│   ├── AlbumsPage.xbf
│   ├── ArtistsPage.xbf
│   ├── EntityDetailPage.xbf
│   ├── PlaylistsPage.xbf
│   ├── PlaylistDetailPage.xbf
│   ├── SearchResultsPage.xbf
│   ├── SettingsPage.xbf
│   └── NowPlayingPage.xbf
│
├── Controls/                        # Compiled Control XAML Binaries
│   ├── SpectrumVisualizerControl.xbf
│   └── NowPlaying/
│       ├── AlbumArtPanel.xbf
│       ├── CreditsPanel.xbf
│       ├── LyricsPanel.xbf
│       └── QueuePanel.xbf
│
├── Assets/                          # High-resolution branding, icons, and placeholder art
│   ├── AppIcon.ico
│   ├── PlaceholderAlbum.png
│   ├── PlaceholderArtist.png
│   ├── SplashScreen.scale-200.png
│   ├── Square44x44Logo.*
│   └── Square150x150Logo.*
│
├── bass.dll                         # Native unmanaged BASS 2.4 64-bit Core Audio Engine
├── bass_fx.dll                      # BASS FX DSP library (DX8 Equalizer, Peak Limiter)
├── bassflac.dll                     # FLAC decoder add-on
├── bassopus.dll                     # Opus decoder add-on
├── bass_aac.dll                     # AAC / M4A decoder add-on
├── bassalac.dll                     # Apple Lossless (ALAC) decoder add-on
├── basswma.dll                      # Windows Media Audio decoder add-on
├── bassdsd.dll                      # Direct Stream Digital (DSD / DSF / DFF) decoder add-on
├── bassape.dll                      # Monkey's Audio (APE) decoder add-on
├── basswv.dll                       # WavPack decoder add-on
├── tags.dll                         # Unmanaged BASS tags extraction helper
│
├── e_sqlite3.dll                    # Native SQLite C library with WAL & FTS5 full-text search
├── Microsoft.ui.xaml.dll            # WinUI 3 native rendering pipeline
├── Microsoft.WindowsAppRuntime.*    # Windows App SDK self-contained runtime bootstrap
├── coreclr.dll / clrjit.dll         # Bundled .NET 10 64-bit runtime (Zero prerequisite installs)
└── ...                              # All supporting runtime and system assemblies
```

---

## 2. Key Architecture Invariants of the Portable Package

1. **Zero External Dependencies / Prerequisites**:
   - The user does **not** need the .NET 10 SDK or Desktop Runtime installed on Windows.
   - The user does **not** need the Windows App SDK runtime or MSIX package installed.
   - All C++ unmanaged runtimes, DirectX / DirectManipulation / DWriteCore components, and BASS native decoders are bundled side-by-side in `Octave_Package/`.

2. **Full Relocatability**:
   - The `Octave_Package/` folder can be copied, moved, zipped, or placed on external USB storage.
   - The app uses `AppContext.BaseDirectory` for relative asset loading, BASS plugin discovery, and WinUI 3 PRI resource loading.
   - The app persists user data (SQLite database `octave.db`, artwork cache `ArtworkCache/`, and logs) in `%LOCALAPPDATA%/Octave/`, ensuring the application folder remains immutable and permissions-safe even if moved to read-only paths or `C:\Program Files`.

3. **WinUI 3 Unpackaged Publish Target Automation**:
   - Defined in [Octave.Desktop.csproj](file:///c:/Users/ADITYA/Documents/GitHub/OCTAVE/Octave.Desktop/Octave.Desktop.csproj) under `PublishWinUIAppArtifacts`:
     - Copies `$(TargetName).pri` and `resources.pri` into the publish directory.
     - Copies all `.xbf` files (root, `Views/`, `Controls/`, `Controls/NowPlaying/`) preserving subdirectory structure.
     - Copies all production `Assets/` (branding, default placeholders, icons) while excluding test fixtures (`test.mp3`) in Release builds.

4. **Audio Engine & Decoder Subsystems**:
   - Supports 23 canonical audio formats natively.
   - BASS decoders (`bassflac.dll`, `bassopus.dll`, `bass_aac.dll`, `bassalac.dll`, `basswma.dll`, `bassdsd.dll`, `bassape.dll`, `basswv.dll`) load automatically from `AppContext.BaseDirectory`.
   - 10-band hardware equalizer and real-time FFT spectrum visualizer operate at zero allocations per frame.

---

## 3. How to Reproduce / Rebuild the Package

To build and package OCTAVE into `Octave_Package/` from the repository root:

```powershell
dotnet publish Octave.Desktop/Octave.Desktop.csproj -c Release -r win-x64 --self-contained true -o Octave_Package
```

To run the standalone test suite verifying packaging integrity:

```powershell
dotnet test --filter "FullyQualifiedName~PackagingAndPublishingIntegrityTests"
```
