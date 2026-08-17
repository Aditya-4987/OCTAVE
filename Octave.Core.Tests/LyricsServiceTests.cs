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

        // Binary search logic test
        int FindIndex(double posSec)
        {
            TimeSpan currentPos = TimeSpan.FromSeconds(posSec);
            int low = 0;
            int high = lines.Count - 1;
            int found = -1;

            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (lines[mid].Start <= currentPos)
                {
                    found = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return found;
        }

        Assert.Equal(-1, FindIndex(2.0));   // Before start
        Assert.Equal(0, FindIndex(5.5));    // Line 1
        Assert.Equal(1, FindIndex(12.0));   // Line 2
        Assert.Equal(2, FindIndex(25.0));   // Line 3
        Assert.Equal(3, FindIndex(45.0));   // Line 4
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
