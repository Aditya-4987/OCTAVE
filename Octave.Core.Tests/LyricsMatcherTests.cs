using System;
using System.Collections.Generic;
using Octave.Core.Models;
using Octave.Core.Services.Metadata;
using Xunit;

namespace Octave.Core.Tests;

public class LyricsMatcherTests
{
    [Theory]
    [InlineData("Dilliwaali Girlfriend (From \"Yeh Jawaani Hai Deewani\")", "dilliwaali girlfriend")]
    [InlineData("Kabira (From 'Yeh Jawaani Hai Deewani')", "kabira")]
    [InlineData("Channa Mereya [From \"Ae Dil Hai Mushkil\"]", "channa mereya")]
    [InlineData("Believer (feat. Lil Wayne)", "believer")]
    [InlineData("In The End (Remastered 2020)", "in the end")]
    [InlineData("Hotel California - Single Version", "hotel california")]
    [InlineData("Tum Hi Ho (Original Motion Picture Soundtrack)", "tum hi ho")]
    public void NormalizeTitle_StripsSoundtrackAndEditionTags(string input, string expected)
    {
        string normalized = LyricsMatcher.NormalizeTitle(input);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void ParseArtistSet_HandlesMultipleSeparatorsAndOrdering()
    {
        string localArtists = "Arijit Singh; Sunidhi Chauhan; Pritam";
        string lrclibArtists = "Pritam, Arijit Singh, Sunidhi Chauhan";

        var set1 = LyricsMatcher.ParseArtistSet(localArtists);
        var set2 = LyricsMatcher.ParseArtistSet(lrclibArtists);

        Assert.Equal(3, set1.Count);
        Assert.Equal(3, set2.Count);
        // ParseArtistSet preserves first-seen order — the primary artist (index 0)
        // must be the first-listed name, deterministically (not HashSet-arbitrary).
        Assert.Equal(new[] { "arijit singh", "sunidhi chauhan", "pritam" }, set1);
        Assert.True(set1.All(set2.Contains) && set2.All(set1.Contains));

        double overlap = LyricsMatcher.CalculateArtistOverlap(set1, set2, localArtists, lrclibArtists);
        Assert.Equal(1.0, overlap);
    }

    [Fact]
    public void DilliwaaliGirlfriend_EdgeCase_MatchesCorrectLrclibRecord()
    {
        // Local metadata with soundtrack attribution, differing artist delimiter/order, and compilation album
        var track = new Track(
            "t-dilliwaali",
            "Dilliwaali Girlfriend (From \"Yeh Jawaani Hai Deewani\")",
            "ar1",
            "Arijit Singh; Sunidhi Chauhan; Pritam",
            "al1",
            "Sunidhi's Sassy Hits",
            260.0, // 4:20
            "http://test/dilliwaali.mp3",
            1,
            2013,
            DateTime.UtcNow);

        var correctCandidate = new LrclibResponse(
            1001,
            "Dilliwaali Girlfriend",
            "Pritam, Arijit Singh, Sunidhi Chauhan",
            "Yeh Jawaani Hai Deewani (Original Motion Picture Soundtrack)",
            260.0,
            false,
            "Where is my dilliwali girlfriend...",
            "[00:10.00]Where is my dilliwali girlfriend...");

        var wrongCandidate = new LrclibResponse(
            1002,
            "Girlfriend",
            "Avril Lavigne",
            "The Best Damn Thing",
            217.0,
            false,
            "Hey hey you you I don't like your girlfriend...",
            null);

        var candidates = new List<LrclibResponse> { wrongCandidate, correctCandidate };

        var (selected, score) = LyricsMatcher.SelectBestCandidate(
            track.Title,
            track.ArtistName,
            track.AlbumTitle,
            track.DurationSeconds,
            candidates);

        Assert.NotNull(selected);
        Assert.Equal(1001, selected.Id);
        Assert.True(score > 0.85, $"Expected score > 0.85 but got {score}");
    }

    [Fact]
    public void SelectBestCandidate_AmbiguousDurations_SelectsClosestDurationNotFirstResult()
    {
        // Local track duration is 260s (4:20)
        var track = new Track(
            "t-hero",
            "Hero",
            "ar1",
            "Enrique Iglesias",
            "al1",
            "Escape",
            260.0, // 4:20
            "http://test/hero.mp3",
            1,
            2001,
            DateTime.UtcNow);

        // Candidate 1: Radio Edit (3:15 = 195s) - appears first in search results
        var radioEdit = new LrclibResponse(
            2001,
            "Hero",
            "Enrique Iglesias",
            "Hero (Radio Edit)",
            195.0,
            false,
            "I can be your hero baby...",
            "[00:05.00]Radio edit synced");

        // Candidate 2: Album Version (4:20 = 260s) - exact match for local track
        var albumVersion = new LrclibResponse(
            2002,
            "Hero",
            "Enrique Iglesias",
            "Escape",
            260.0,
            false,
            "I can be your hero baby...",
            "[00:05.00]Album version synced");

        // Candidate 3: Extended Club Mix (6:00 = 360s)
        var extendedMix = new LrclibResponse(
            2003,
            "Hero",
            "Enrique Iglesias",
            "Hero (Club Mixes)",
            360.0,
            false,
            "I can be your hero baby...",
            "[00:05.00]Extended mix synced");

        var candidates = new List<LrclibResponse> { radioEdit, albumVersion, extendedMix };

        var (selected, score) = LyricsMatcher.SelectBestCandidate(
            track.Title,
            track.ArtistName,
            track.AlbumTitle,
            track.DurationSeconds,
            candidates);

        Assert.NotNull(selected);
        // Must select Candidate 2 (Album Version) because duration matches 260s, NOT Candidate 1 just because it was first!
        Assert.Equal(2002, selected.Id);
        Assert.Equal(260.0, selected.Duration);
    }

    [Fact]
    public void SelectBestCandidate_RejectsCandidatesBelowThreshold()
    {
        var track = new Track(
            "t-bohemian",
            "Bohemian Rhapsody",
            "ar1",
            "Queen",
            "al1",
            "A Night at the Opera",
            354.0,
            "http://test/bohemian.mp3",
            1,
            1975,
            DateTime.UtcNow);

        // Search returned completely unrelated tracks
        var unrelated = new LrclibResponse(
            3001,
            "Hungarian Rhapsody",
            "Franz Liszt",
            "Classical Hits",
            600.0,
            true,
            null,
            null);

        var (selected, score) = LyricsMatcher.SelectBestCandidate(
            track.Title,
            track.ArtistName,
            track.AlbumTitle,
            track.DurationSeconds,
            new[] { unrelated },
            minScore: 0.60);

        Assert.Null(selected);
        Assert.True(score < 0.60);
    }
}
