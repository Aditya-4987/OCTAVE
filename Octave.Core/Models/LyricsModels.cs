using System;
using System.Collections.Generic;

namespace Octave.Core.Models;

public record LyricLine(TimeSpan Start, TimeSpan? End, string Text);

public enum LyricsState { Loading, Synced, Unsynced, Unavailable }

public record LyricsData(
    string? TrackId,
    LyricsState State,
    IReadOnlyList<LyricLine>? SyncedLines,
    string? PlainText
);
