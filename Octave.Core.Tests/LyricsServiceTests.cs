using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;
using Octave.Core.Services.Metadata;
using Xunit;

namespace Octave.Core.Tests;

public class LyricsServiceTests
{
    [Fact]
    public void ParseLrcContent_ValidTimestampedLrc_ReturnsSyncedLyricsWithOrderedLines()
    {
        string rawLrc = @"[ti:Test Song]
[ar:Octave Artist]
[00:04.50]First line of lyrics
[00:09.10]Second line of lyrics
[00:15.00]Third line of lyrics";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Synced, data.State);
        Assert.NotNull(data.SyncedLines);
        Assert.Equal(3, data.SyncedLines.Count);

        Assert.Equal(TimeSpan.FromSeconds(4.5), data.SyncedLines[0].Start);
        Assert.Equal("First line of lyrics", data.SyncedLines[0].Text);

        Assert.Equal(TimeSpan.FromSeconds(9.1), data.SyncedLines[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(9.1), data.SyncedLines[0].End);

        Assert.Equal(TimeSpan.FromSeconds(15.0), data.SyncedLines[2].Start);
        Assert.Null(data.SyncedLines[2].End);
    }

    [Fact]
    public void ParseLrcContent_PlainTextLrc_ReturnsUnsyncedLyrics()
    {
        string rawLrc = @"Line one of unsynced lyrics
Line two of unsynced lyrics
Line three of unsynced lyrics";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Unsynced, data.State);
        Assert.Null(data.SyncedLines);
        Assert.Contains("Line one", data.PlainText);
        Assert.Contains("Line three", data.PlainText);
    }

    [Fact]
    public void ParseLrcContent_EmptyOrInvalidString_ReturnsUnavailable()
    {
        var data = LyricsService.ParseLrcContent("track_1", "   ");
        Assert.Equal(LyricsState.Unavailable, data.State);
        Assert.Null(data.SyncedLines);
        Assert.Null(data.PlainText);
    }

    // TEST-04: this used to re-implement the binary search locally, which meant a
    // regression in the production lookup would never be caught. Drive the real
    // LyricsService.FindActiveLineIndex instead — including the empty/null guards.
    [Fact]
    public void LyricSynchronization_BoundaryCheck_And_BinarySearchSeek_WorkAccurately()
    {
        var lines = new List<LyricLine>
        {
            new LyricLine(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), "Line 1"),
            new LyricLine(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), "Line 2"),
            new LyricLine(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), "Line 3"),
            new LyricLine(TimeSpan.FromSeconds(30), null, "Line 4")
        };

        // Guards first: null list, empty list.
        Assert.Equal(-1, LyricsService.FindActiveLineIndex(null!, TimeSpan.FromSeconds(10)));
        Assert.Equal(-1, LyricsService.FindActiveLineIndex(new List<LyricLine>(), TimeSpan.FromSeconds(10)));

        // Exact boundaries land on the later line when timestamps coincide.
        Assert.Equal(-1, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(2.0)));  // Before start
        Assert.Equal(-1, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(4.99))); // Just before Line 1
        Assert.Equal(0, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(5.0)));   // Exactly Line 1 start
        Assert.Equal(0, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(5.5)));   // Line 1
        Assert.Equal(1, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(10.0)));  // Exactly Line 2 start
        Assert.Equal(1, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(12.0)));  // Line 2
        Assert.Equal(2, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(20.0)));  // Exactly Line 3 start
        Assert.Equal(2, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(25.0)));  // Line 3
        Assert.Equal(3, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(30.0)));  // Exactly Line 4 start
        Assert.Equal(3, LyricsService.FindActiveLineIndex(lines, TimeSpan.FromSeconds(45.0)));  // Line 4 (open-ended)

        // Unsorted input must not throw or loop forever — last matching Start wins.
        var shuffled = new List<LyricLine>(lines);
        (shuffled[1], shuffled[2]) = (shuffled[2], shuffled[1]);
        int idx = LyricsService.FindActiveLineIndex(shuffled, TimeSpan.FromSeconds(25.0));
        Assert.True(idx is 1 or 2, $"expected an active-line index in [1,2], got {idx}");
    }

    // TEST-10: fractional-timestamp variants and multi-timestamp lines that real
    // .lrc files contain; plus the offset clamping rule for pre-zero results.
    [Theory]
    [InlineData("00:04.5", 4.5)]      // single millisecond digit → ×100
    [InlineData("00:04.50", 4.5)]     // two digits → ×10
    [InlineData("00:04.500", 4.5)]    // three digits taken as-is
    [InlineData("01:04.5", 64.5)]     // minutes carry over
    public void ParseLrcContent_FractionalTimestampVariants_ParseToExactStarts(string stamp, double expectedSeconds)
    {
        string rawLrc = $"[{stamp}]Fractional line";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Synced, data.State);
        Assert.NotNull(data.SyncedLines);
        LyricLine line = Assert.Single(data.SyncedLines);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), line.Start);
    }

    [Fact]
    public void ParseLrcContent_MultiTimestampLine_CreatesOneEntryPerTimestamp()
    {
        string rawLrc = "[00:01.00][00:05.00]Shared chorus";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Synced, data.State);
        Assert.NotNull(data.SyncedLines);
        Assert.Equal(2, data.SyncedLines.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), data.SyncedLines[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(5), data.SyncedLines[1].Start);
        Assert.All(data.SyncedLines, l => Assert.Equal("Shared chorus", l.Text));
    }

    [Fact]
    public void ParseLrcContent_OffsetPushingBeforeZero_ClampsToZero()
    {
        string rawLrc = @"[offset:+6000]
[00:04.00]Clamped line
[00:09.00]Still positive line";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Synced, data.State);
        Assert.NotNull(data.SyncedLines);
        Assert.Equal(TimeSpan.Zero, data.SyncedLines[0].Start);          // 4000-6000 → clamped
        Assert.Equal(TimeSpan.FromSeconds(3), data.SyncedLines[1].Start); // 9000-6000 stays exact
    }

    [Fact]
    public void ParseLrcContent_WithPositiveOffset_ShiftsTimestampsEarlier()
    {
        string rawLrc = @"[offset:+500]
[00:05.00]First line
[00:10.00]Second line";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Synced, data.State);
        Assert.NotNull(data.SyncedLines);
        Assert.Equal(2, data.SyncedLines.Count);

        // 5000ms - 500ms = 4500ms (4.5s)
        Assert.Equal(TimeSpan.FromSeconds(4.5), data.SyncedLines[0].Start);
        // 10000ms - 500ms = 9500ms (9.5s)
        Assert.Equal(TimeSpan.FromSeconds(9.5), data.SyncedLines[1].Start);
    }

    [Fact]
    public void ParseLrcContent_WithNegativeOffset_ShiftsTimestampsLater()
    {
        string rawLrc = @"[offset:-1000]
[00:05.00]First line
[00:10.00]Second line";

        var data = LyricsService.ParseLrcContent("track_1", rawLrc);

        Assert.Equal(LyricsState.Synced, data.State);
        Assert.NotNull(data.SyncedLines);
        Assert.Equal(2, data.SyncedLines.Count);

        // 5000ms - (-1000ms) = 6000ms (6.0s)
        Assert.Equal(TimeSpan.FromSeconds(6.0), data.SyncedLines[0].Start);
        // 10000ms - (-1000ms) = 11000ms (11.0s)
        Assert.Equal(TimeSpan.FromSeconds(11.0), data.SyncedLines[1].Start);
    }
}
