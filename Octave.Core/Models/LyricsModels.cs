using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Octave.Core.Models;

public record LyricLine(TimeSpan Start, TimeSpan? End, string Text);

public enum LyricsState
{
    Loading,
    Resolving,
    Synced,
    Unsynced,
    Unavailable,
    NetworkUnavailable
}

public enum LyricDisplayMode { Synced, Static }

public record LyricsData(
    string? TrackId,
    LyricsState State,
    IReadOnlyList<LyricLine>? SyncedLines,
    string? PlainText,
    bool HasSyncedLyrics = false,
    bool HasPlainLyrics = false,
    string? RawSyncedLyrics = null,
    bool IsNetworkError = false,
    string? SyncedSource = null,
    string? StaticSource = null,
    string? Source = null
);

public record LrclibResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("artistName")] string? ArtistName,
    [property: JsonPropertyName("albumName")] string? AlbumName,
    [property: JsonPropertyName("duration")] double? Duration,
    [property: JsonPropertyName("instrumental")] bool Instrumental,
    [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
    [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics,
    [property: JsonPropertyName("name")] string? Name = null
);

