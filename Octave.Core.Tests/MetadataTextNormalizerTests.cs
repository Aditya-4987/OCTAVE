using Xunit;

namespace Octave.Core.Tests;

// TEST-15: the fuzzy-match core (normalize → similarity → version detection →
// filename parsing) was previously exercised only indirectly through matcher
// integration tests. These table-driven theories pin each Batch 7 behavior:
// MATCH-02 (containment floor), MATCH-04 (hyphen splitting) and MATCH-05
// (title-only version detection).
public class MetadataTextNormalizerTests
{
    // =================================================================
    // 1. CALCULATE SIMILARITY — TABLE-DRIVEN (MATCH-02)
    //    Each row: (inputA, inputB, minInclusive, maxInclusive)
    // =================================================================

    public static TheoryData<string?, string?, double, double> SimilarityCases => new()
    {
        // Exact & normalization folds
        { "Bohemian Rhapsody", "Bohemian Rhapsody", 1.0, 1.0 },   // identical
        { "Jóga", "Joga", 1.0, 1.0 },                             // diacritics fold
        { "Rock 'N' Roll Train", "rock n roll train", 1.0, 1.0 }, // case/punctuation fold
        { "AC/DC", "AC DC", 1.0, 1.0 },                           // '/' normalizes to space

        // MATCH-02: weak containment must NOT floor at 0.85 — a 2-character
        // fragment inside a longer word falls through to Levenshtein and
        // scores on its own (low) merits.
        { "goodbye", "go", 0.0, 0.45 },

        // Whole-word containment remains a strong signal (≥ 0.85).
        { "live version", "live", 0.85, 1.0 },
        { "black dog", "black", 0.85, 1.0 },                      // ratio >= 0.5 boost
        { "black", "black dog", 0.85, 1.0 },                      // symmetric

        // Dissimilar strings stay low.
        { "abbey road", "the dark side of the moon", 0.0, 0.35 },
        { "", "anything at all", 0.0, 0.0 },
    };

    [Theory]
    [MemberData(nameof(SimilarityCases))]
    public void CalculateSimilarity_TableCases_ScoreWithinExpectedBand(
        string? inputA, string? inputB, double minExpected, double maxExpected)
    {
        double score = Octave.Core.Helpers.MetadataTextNormalizer.CalculateSimilarity(inputA, inputB);

        Assert.InRange(score, minExpected, maxExpected);
    }

    [Fact]
    public void CalculateSimilarity_WeakFragmentInsideWord_IsDemotedBelowAutoApplyGate()
    {
        // The concrete MATCH-02 failure mode: "go" vs "Goodbye" used to clear
        // the 0.85 auto-apply gate purely because of substring containment.
        double score = Octave.Core.Helpers.MetadataTextNormalizer.CalculateSimilarity("goodbye", "go");

        Assert.True(score < 0.70, $"Expected weak fragment to score < 0.70, got {score}");
    }

    // =================================================================
    // 2. VERSION DETECTION — TABLE-DRIVEN (MATCH-05)
    //    Each row: (title, album, expectedLive, expectedRemix, expectedAcoustic)
    // =================================================================

    public static TheoryData<string?, string?, bool, bool, bool> VersionDetectionCases => new()
    {
        // Album-only markers must NOT assert a version (MATCH-05).
        { "Yesterday", "Live at Wembley", false, false, false },
        { "Comfortably Numb", "Pulse (Live)", false, false, false },
        { "Levitating", "Remix Collection", false, false, false },
        { "No More Sorrow", "Unplugged Sessions", false, false, false },

        // Title markers still do.
        { "Yesterday (Live)", null, true, false, false },
        { "Levitating (Remix)", null, false, true, false },
        { "No More Sorrow (Acoustic)", null, false, false, true },

        // Plain titles stay clean regardless of album.
        { "Yesterday", null, false, false, false },
    };

    [Theory]
    [MemberData(nameof(VersionDetectionCases))]
    public void ExtractVersionInfo_TableCases_VersionComesFromTitleOnly(
        string? title, string? album, bool expectedLive, bool expectedRemix, bool expectedAcoustic)
    {
        var info = Octave.Core.Helpers.MetadataTextNormalizer.ExtractVersionInfo(title, album);

        Assert.Equal(expectedLive, info.IsLive);
        Assert.Equal(expectedRemix, info.IsRemix);
        Assert.Equal(expectedAcoustic, info.IsAcoustic);
    }

    // =================================================================
    // 3. FILENAME PARSING — TABLE-DRIVEN (MATCH-04)
    //    Each row: (path, expectedTitle, expectedArtist, expectedTrackNumber)
    // =================================================================

    public static TheoryData<string, string?, string?, int?> FilenameParseCases => new()
    {
        // MATCH-04 headline: an intra-word hyphen must not split artist/title —
        // "Spider-Man Theme" is a TITLE, not artist "Spider" / title "Man Theme".
        { @"C:\music\Spider-Man Theme.mp3", "Spider-Man Theme", null, null },

        // Spaced-dash patterns still parse, with or without a track number.
        { @"C:\music\01 - Artist Name - Song Title.mp3", "Song Title", "Artist Name", 1 },
        { @"C:\music\01. Queen - Bohemian Rhapsody.mp3", "Bohemian Rhapsody", "Queen", 1 },

        // Number + title without any dash.
        { @"C:\music\07_Title.flac", "Title", null, 7 },

        // Plain filename fallback.
        { @"C:\music\Plain Track.mp3", "Plain Track", null, null },

        // Hyphenated artist names survive when separated by spaced dashes.
        { @"C:\music\Nick Cave & The Bad Seeds - Red Right Hand.mp3", "Red Right Hand", "Nick Cave & The Bad Seeds", null },
    };

    [Theory]
    [MemberData(nameof(FilenameParseCases))]
    public void ParseFromPath_TableCases_ExtractsExpectedParts(
        string filePath, string? expectedTitle, string? expectedArtist, int? expectedTrackNumber)
    {
        var (title, artist, _, trackNumber) = Octave.Core.Helpers.MetadataTextNormalizer.ParseFromPath(filePath);

        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedArtist, artist);
        Assert.Equal(expectedTrackNumber, trackNumber);
    }

    [Fact]
    public void ParseFromPath_DirectoryName_BecomesAlbumDefault()
    {
        var (title, artist, album, trackNumber) = Octave.Core.Helpers.MetadataTextNormalizer.ParseFromPath(
            @"C:\lib\A Night at the Opera\02 - Queen - You're My Best Friend.mp3");

        Assert.Equal("You're My Best Friend", title);
        Assert.Equal("Queen", artist);
        Assert.Equal("A Night at the Opera", album);
        Assert.Equal(2, trackNumber);
    }
}
