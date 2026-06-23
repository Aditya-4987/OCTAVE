# Octave.Core

This is the core business logic and domain layer for the OCTAVE media player. It is written in C# targeting .NET 10.0. It is designed to be a strictly decoupled library containing models, helpers, data repositories, and audio player engine implementations.

## Directory Structure

- **[Helpers/](./Helpers)**: Utility classes, including unique string ID generators and local file age resolvers.
- **[Interfaces/](./Interfaces)**: Core abstract service contracts.
  - [IAudioPlayerService](./Services/Audio/IAudioPlayerService.cs): Audio player engine lifecycle, seeking, and playback state properties.
  - [IQueueService](./Interfaces/IQueueService.cs): Playback queue management, playlist reordering, repeat modes, and shuffling state-machine.
- **[Models/](./Models)**: Persistent entities mapping 1:1 to STRICT SQLite tables (Tracks, Albums, Artists, Playlists) and runtime models (QueueItem, PlaybackState, PlaybackStatus, RepeatMode).
- **[Services/](./Services)**: Concrete implementations of core business features:
  - **[Audio/](./Services/Audio)**: `ManagedBassAudioService.cs` is a fully decoupled C# wrapper around the unmanaged native BASS library. Handles hardware initializations, unmanaged memory release, volume, clamped seeks, and thread-safe callbacks.
  - **[Database/](./Services/Database)**: `SqliteDbContext.cs` is a local SQLite database repository utilizing standard ADO.NET SQLite readers and parameterized queries inside transactions to protect against SQL injections.
  - **[Library/](./Services/Library)**: Contains `ILibraryService`, `LocalLibraryScanner`, and `LibraryService`. Handles local folder crawling using a memory-efficient $O(1)$ Producer-Consumer pattern (`System.Threading.Channels`), TagLib# metadata parsing, and set-based DB reconciliation/hygiene sweeps.
  - **[Playback/](./Services/Playback)**: `QueueService.cs` coordinates queue transitions, repeat cycles (None/Track/Queue), Fisher-Yates shuffle algorithms, playback history database logging, and non-destructive disk existence checks (protection valve).
