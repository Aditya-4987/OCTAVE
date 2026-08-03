using System;

namespace Octave.Core.Models;

// =================================================================
// 1. PERSISTED ENTITIES (Mapped 1:1 to SQLite STRICT Tables)
// =================================================================

public record Artist(
    string Id,
    string Name,
    string? Bio,
    string? ArtworkUrl,
    bool IsLocal
);

public record Album(
    string Id,
    string Title,
    string ArtistId,
    string ArtistName,
    int Year,
    string? ArtworkUrl,
    string Provider
);

public record Track(
    string Id,
    string Title,
    string ArtistId,
    string ArtistName,
    string AlbumId,
    string AlbumTitle,
    double DurationSeconds,
    string SourceUri,       // Local absolute file path OR remote CDN URL
    string Provider,        // "Local", "Qobuz", "Tidal", "YouTube"
    int TrackNumber,
    int Year,
    DateTime DateAdded,
    string Genre = "",      // Trailing default so existing constructors are unaffected
    double ReplayGain = 0.0 // Applied in playback to normalize loudness
);

public record Playlist(
    string Id,
    string Title,
    string? Description,
    DateTime CreatedAt,
    bool IsLocalOnly,
    int TrackCount = 0      // Hydrated dynamically by SQL COUNT()
);

// =================================================================
// 2. RUNTIME / STATE MODELS (Never saved to SQL directly)
// =================================================================

public class QueueItem
{
    public required string Id { get; init; }
    public required Track Track { get; init; }
    public bool IsPlaying { get; set; }
}

public enum PlaybackStatus { Stopped, Playing, Paused, Buffering }

public enum RepeatMode { None, Track, Queue }

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

public enum EntityType { Album, Artist, Track, Genre, Playlist }
public record EntityNavigationParameter(EntityType Type, string Id);

// Snapshot of the player used to resume the queue after an app restart.
public record PersistedPlayerState(
    System.Collections.Generic.List<string> TrackIds,
    int CurrentIndex,
    double PositionSeconds,
    float Volume,
    bool IsShuffle,
    RepeatMode RepeatMode
);

public record SearchResults(
    System.Collections.Generic.List<Track> Tracks,
    System.Collections.Generic.List<Album> Albums,
    System.Collections.Generic.List<Artist> Artists,
    System.Collections.Generic.List<Playlist>? Playlists = null
);
public record SearchSuggestion(string Title, EntityType Type, string Id);

// A cluster of tracks that appear to be the same composition across formats.
public record DuplicateGroup(string Title, string ArtistName, System.Collections.Generic.List<Track> Tracks);