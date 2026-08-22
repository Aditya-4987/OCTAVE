# OCTAVE

A native Windows music player for audiophiles, built with **WinUI 3** (.NET 10) and the **BASS** audio engine.

## Features

- Local library scanning (MP3/FLAC/OGG/WAV via TagLib#), with folder watching for live updates
- BASS-powered playback: gapless/crossfade, 10-band EQ + limiter, ReplayGain, WASAPI output info
- Now Playing experience: synced lyrics (local LRC + LRCLIB), queue management, spectrum visualizer
- Online metadata enrichment: MusicBrainz, Cover Art Archive, TheAudioDB — with a two-tier cache,
  offline mode, and confidence-gated auto-tagging that never overwrites your tags without consent
- Playlists, favorites, playback history, mini player, and Windows System Media Transport Controls

## Building

Requires **Windows 10 19041+** (Windows 11 recommended) with the
[Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/) and .NET 10 SDK.

```powershell
dotnet build Octave.Desktop          # build the app
dotnet test Octave.Core.Tests        # run the test suite
```

Run from Visual Studio 2026 (open `OCTAVE.slnx`) or `dotnet run --project Octave.Desktop`.

Optional: drop decoder add-ons (`bassflac.dll`, `bassopus.dll`, …) into `Octave.Desktop\BassPlugins\`
and `bass_fx.dll` next to `bass.dll` to enable additional formats and the FX engine.

## Architecture

| Project | Role |
|---|---|
| **Octave.Core** | Domain models, SQLite persistence (`SqliteDbContext`), library scanner/watcher, BASS audio service (`ManagedBassAudioService`), playback queue, metadata editors, external-data providers/orchestrators |
| **Octave.Desktop** | WinUI 3 shell: MVVM ViewModels (CommunityToolkit.Mvvm), pages (Home/Library/Albums/Artists/Playlists/Search/Settings/Now Playing), custom controls (lyrics panel, queue, visualizer), dialogs |
| **Octave.Core.Tests** | xUnit test suite (unit + on-disk integration tests against real SQLite/temp-file fixtures) |

Patterns: services are DI singletons registered in `App.xaml.cs`; ViewModels are transient and
event-driven off `IQueueService`/`IAudioPlayerService` state broadcasts. All persistence goes through
`SqliteDbContext` (WAL, STRICT tables, FTS5 search).
