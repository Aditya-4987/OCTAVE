using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Octave.Core.Helpers;
using Octave.Core.Models;
using Octave.Core.Services.External;
using Xunit;

namespace Octave.Core.Tests;

public class TrackMetadataMatcherTests
{
    // =================================================================
    // 1. EXACT MATCH
    // =================================================================

    [Fact]
    public void ScoreCandidate_ExactMatch_YieldsHighConfidence()
    {
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();
        var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);

        var localTrack = new Track("tr1", "Bohemian Rhapsody", "ar1", "Queen", "al1", "A Night at the Opera", 354.0, "C:/music/bohemian.flac", "Local", 11, 1975, DateTime.UtcNow);

        var candidate = new ExternalTrackMetadata(
            "Bohemian Rhapsody",
            "Queen",
            "A Night at the Opera",
            1975,
            "Rock",
            11,
            1,
            354.2,
            "GBUM71029603",
            new ExternalIds("mb-rec-123"));

        var result = matcher.ScoreCandidate(localTrack, candidate, "MusicBrainz");

        Assert.True(result.Confidence >= 0.95, $"Expected confidence >= 0.95, got {result.Confidence}");
        Assert.False(result.IsVersionMismatch);
        Assert.Contains("Exact Title Match", result.MatchEvidence);
        Assert.Contains("Exact Artist Match", result.MatchEvidence);
        Assert.Contains("Exact Duration", result.MatchEvidence);
    }

    // =================================================================
    // 2. PUNCTUATION DIFFERENCES
    // =================================================================

    [Fact]
    public void ScoreCandidate_PunctuationDifferences_MatchesCorrectly()
    {
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();
        var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);

        var localTrack = new Track("tr1", "Rock 'N' Roll Train", "ar1", "AC/DC", "al1", "Black Ice", 261.0, "C:/music/track.mp3", "Local", 1, 2008, DateTime.UtcNow);

        var candidate = new ExternalTrackMetadata(
            "Rock N Roll Train",
            "AC DC",
            "Black Ice",
            2008,
            "Hard Rock",
            1,
            1,
            261.0,
            null,
            ExternalIds.Empty);

        var result = matcher.ScoreCandidate(localTrack, candidate, "MusicBrainz");

        // Normalized matching strips punctuation and apostrophes
        Assert.True(result.Confidence >= 0.90, $"Expected confidence >= 0.90, got {result.Confidence}");
    }

    // =================================================================
    // 3. UNICODE & DIACRITICS
    // =================================================================

    [Fact]
    public void ScoreCandidate_UnicodeDiacritics_MatchesAccurately()
    {
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();
        var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);

        // Local track has plain ASCII, candidate has accents (or vice versa)
        var localTrack = new Track("tr1", "Joga", "ar1", "Bjork", "al1", "Homogenic", 305.0, "C:/music/joga.flac", "Local", 2, 1997, DateTime.UtcNow);

        var candidate = new ExternalTrackMetadata(
            "Jóga",
            "Björk",
            "Homogenic",
            1997,
            "Electronic",
            2,
            1,
            305.5,
            null,
            ExternalIds.Empty);

        var result = matcher.ScoreCandidate(localTrack, candidate, "MusicBrainz");

        Assert.True(result.Confidence >= 0.95, $"Expected confidence >= 0.95, got {result.Confidence}");
    }

    // =================================================================
    // 4. DURATION MISMATCH
    // =================================================================

    [Fact]
    public void ScoreCandidate_LargeDurationMismatch_AppliesPenalty()
    {
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();
        var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);

        // Local track is 3 minutes (180s), candidate is an extended 12-minute version (720s)
        var localTrack = new Track("tr1", "Rapper's Delight", "ar1", "The Sugarhill Gang", "al1", "Sugarhill Gang", 180.0, "C:/music/track.mp3", "Local", 1, 1979, DateTime.UtcNow);

        var candidate = new ExternalTrackMetadata(
            "Rapper's Delight",
            "The Sugarhill Gang",
            "Sugarhill Gang",
            1979,
            "Hip Hop",
            1,
            1,
            874.0, // Long version
            null,
            ExternalIds.Empty);

        var result = matcher.ScoreCandidate(localTrack, candidate, "MusicBrainz");

        // Duration penalty must pull confidence below high match
        Assert.True(result.Confidence < 0.70, $"Expected confidence < 0.70 due to duration mismatch, got {result.Confidence}");
        Assert.Contains("Duration Mismatch", result.MatchEvidence);
    }

    // =================================================================
    // 5. REMIX / VERSION MISMATCH
    // =================================================================

    [Fact]
    public void ScoreCandidate_RemixVsOriginal_AppliesVersionMismatchPenalty()
    {
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();
        var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);

        var localTrack = new Track("tr1", "Levitating (Klangkarussell Remix)", "ar1", "Dua Lipa", "al1", "Club Future Nostalgia", 240.0, "C:/music/remix.mp3", "Local", 1, 2020, DateTime.UtcNow);

        var candidateOriginal = new ExternalTrackMetadata(
            "Levitating",
            "Dua Lipa",
            "Future Nostalgia",
            2020,
            "Pop",
            5,
            1,
            203.0,
            null,
            ExternalIds.Empty);

        var result = matcher.ScoreCandidate(localTrack, candidateOriginal, "MusicBrainz");

        Assert.True(result.IsVersionMismatch);
        Assert.True(result.Confidence < 0.60, $"Expected confidence < 0.60 due to version mismatch, got {result.Confidence}");
        Assert.Contains("Version Mismatch", result.MatchEvidence);
    }

    // =================================================================
    // 6. LIVE RECORDING VS STUDIO RECORDING
    // =================================================================

    [Fact]
    public void ScoreCandidate_LiveVsStudio_AppliesLivePenalty()
    {
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();
        var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);

        // Local track is standard studio version
        var localTrack = new Track("tr1", "Comfortably Numb", "ar1", "Pink Floyd", "al1", "The Wall", 382.0, "C:/music/numb.flac", "Local", 6, 1979, DateTime.UtcNow);

        // Candidate is live album recording
        var candidateLive = new ExternalTrackMetadata(
            "Comfortably Numb (Live in Berlin)",
            "Pink Floyd",
            "Pulse (Live)",
            1995,
            "Progressive Rock",
            10,
            2,
            550.0,
            null,
            ExternalIds.Empty);

        var result = matcher.ScoreCandidate(localTrack, candidateLive, "MusicBrainz");

        Assert.True(result.IsVersionMismatch);
        Assert.True(result.Confidence < 0.60, $"Expected confidence < 0.60 due to Live mismatch, got {result.Confidence}");
        Assert.Contains("Live", result.MatchEvidence);
    }

    // =================================================================
    // 7. MULTIPLE CANDIDATE RESULTS & RANKING
    // =================================================================

    [Fact]
    public async Task FindMatchesForTrackAsync_ReturnsRankedCandidates()
    {
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();

        var localTrack = new Track("tr1", "Time", "ar1", "Pink Floyd", "al1", "The Dark Side of the Moon", 413.0, "C:/music/time.flac", "Local", 4, 1973, DateTime.UtcNow);

        var candExact = new TrackMatchCandidate("MusicBrainz", new ExternalIds("mb-1"), 0.5, "Initial",
            new ExternalTrackMetadata("Time", "Pink Floyd", "The Dark Side of the Moon", 1973, "Progressive Rock", 4, 1, 413.0, "GBAYE7300040", new ExternalIds("mb-1")));

        var candLive = new TrackMatchCandidate("MusicBrainz", new ExternalIds("mb-2"), 0.5, "Initial",
            new ExternalTrackMetadata("Time (Live at Wembley)", "Pink Floyd", "Live 1974", 1974, "Rock", 3, 1, 330.0, null, new ExternalIds("mb-2")));

        var candCover = new TrackMatchCandidate("MusicBrainz", new ExternalIds("mb-3"), 0.5, "Initial",
            new ExternalTrackMetadata("Time", "Greenslade", "Cover Album", 2000, "Rock", 1, 1, 413.0, null, new ExternalIds("mb-3")));

        mockOrchestrator.Setup(o => o.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candLive, candCover, candExact });

        var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);

        var ranked = await matcher.FindMatchesForTrackAsync(localTrack);

        Assert.Equal(3, ranked.Count);
        // Studio exact version should be ranked #1 with highest confidence
        Assert.Equal("mb-1", ranked[0].ExternalIds.MusicBrainzId);
        Assert.True(ranked[0].Confidence >= 0.95);

        // Lower ranked candidates
        Assert.True(ranked[0].Confidence > ranked[1].Confidence);
        Assert.True(ranked[1].Confidence >= ranked[2].Confidence);
    }

    // =================================================================
    // 8. AMBIGUOUS MATCH
    // =================================================================

    [Fact]
    public void ScoreCandidate_AmbiguousMatch_YieldsModerateScore()
    {
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();
        var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);

        // Track with minimal information
        var localTrack = new Track("tr1", "Hold On", "ar1", "Alabama Shakes", "al1", "", 228.0, "C:/music/track.mp3", "Local", 1, 2012, DateTime.UtcNow);

        // Candidate with matching title and artist, but different duration and missing album
        var candidate = new ExternalTrackMetadata(
            "Hold On",
            "Alabama Shakes",
            "Unknown Compilation",
            2015,
            null,
            null,
            null,
            245.0, // 17s duration difference
            null,
            ExternalIds.Empty);

        var result = matcher.ScoreCandidate(localTrack, candidate, "MusicBrainz");

        // Moderate confidence (not strong enough to be auto-applied blindly, but
        // a viable candidate). MATCH-06 note: the ±17 s duration difference used
        // to fall in the unscored 15–25 s dead zone; it now takes the explicit
        // mismatch penalty (-0.15), landing this scenario at ~0.50 instead of ~0.65.
        Assert.InRange(result.Confidence, 0.40, 0.70);
    }

    // =================================================================
    // 9. MISSING TAGS (FILENAME PARSING FALLBACK)
    // =================================================================

    [Fact]
    public async Task FindMatchesForTrackAsync_MissingTags_UsesFilenameInformation()
    {
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();

        string capturedSearchTitle = string.Empty;
        string capturedSearchArtist = string.Empty;

        mockOrchestrator.Setup(o => o.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, double?, CancellationToken>((title, artist, album, dur, ct) =>
            {
                capturedSearchTitle = title;
                capturedSearchArtist = artist;
            })
            .ReturnsAsync(new[]
            {
                new TrackMatchCandidate("MusicBrainz", new ExternalIds("mb-recovered"), 0.95, "Found",
                    new ExternalTrackMetadata("Money", "Pink Floyd", "Dark Side", 1973, "Rock", 6, 1, 382.0, null, new ExternalIds("mb-recovered")))
            });

        string tempAudioFile = Path.Combine(Path.GetTempPath(), "06 - Pink Floyd - Money.mp3");
        try
        {
            File.WriteAllText(tempAudioFile, "dummy audio content");

            // Local track with missing/generic tags
            var untaggedTrack = new Track("tr_blank", "Track 06", "ar_blank", "Unknown Artist", "al_blank", "Unknown Album", 382.0, tempAudioFile, "Local", 6, 0, DateTime.UtcNow);

            var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);
            var results = await matcher.FindMatchesForTrackAsync(untaggedTrack);

            Assert.Equal("Money", capturedSearchTitle);
            Assert.Equal("Pink Floyd", capturedSearchArtist);
            Assert.Single(results);
            Assert.Equal("mb-recovered", results[0].ExternalIds.MusicBrainzId);
        }
        finally
        {
            if (File.Exists(tempAudioFile))
            {
                try { File.Delete(tempAudioFile); } catch { }
            }
        }
    }

    // =================================================================
    // 10. EXACT EXTERNAL-ID MATCH (MATCH-01 — Batch 2)
    // =================================================================

    // Minimal parseable MP3 (ID3v2.3 header + one MPEG frame) so TagLib can open,
    // tag and save the fixture — mirrors the fixture used by TrackEnrichmentWorkflowTests.
    private static readonly byte[] ValidMp3Bytes = new byte[] {
        0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    [Fact]
    public void ScoreCandidate_ExactMbidFromLocalTags_ShortCircuitsToPerfectConfidence()
    {
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();
        var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);

        // Local fuzzy fields are garbage — only the file's stored MBID is reliable.
        var localTrack = new Track("hash_of_path", "Xkcd Fuzzy Title", "ar9", "Wrong Artist", "al9", "Wrong Album", 111.0, "C:/music/x.mp3", "Local", 0, 0, DateTime.UtcNow);
        var localIds = new ExternalIds(MusicBrainzId: "mbid-target-123");

        var candidate = new ExternalTrackMetadata(
            "Xkcd Fuzzy Title", "Wrong Artist", "Wrong Album", 0, null, 0, null, 111.0, null,
            new ExternalIds(MusicBrainzId: "mbid-target-123"));

        var result = matcher.ScoreCandidate(localTrack, candidate, "MusicBrainz", candidate.ExternalIds, localIds);

        Assert.Equal(1.0, result.Confidence);
        Assert.True(result.IsExactIdMatch);
        Assert.Contains("Exact MusicBrainz ID match", result.MatchEvidence);
    }

    [Fact]
    public void ScoreCandidate_ExactIsrcFromLocalTags_ShortCircuitsToPerfectConfidence()
    {
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();
        var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);

        var localTrack = new Track("hash_of_path", "Fuzzy Title", "ar9", "Wrong Artist", "al9", "Wrong Album", 111.0, "C:/music/x.mp3", "Local", 0, 0, DateTime.UtcNow);
        var localIds = new ExternalIds(Isrc: "GBUM71029603");

        var candidate = new ExternalTrackMetadata(
            "Fuzzy Title", "Wrong Artist", "Wrong Album", 0, null, 0, null, 111.0, "GBUM71029603",
            new ExternalIds(Isrc: "gbum71029603")); // case-insensitive on purpose

        var result = matcher.ScoreCandidate(localTrack, candidate, "Spotify", candidate.ExternalIds, localIds);

        Assert.Equal(1.0, result.Confidence);
        Assert.True(result.IsExactIdMatch);
        Assert.Contains("Exact ISRC match", result.MatchEvidence);
    }

    [Fact]
    public void ScoreCandidate_PathHashIdMatchingCandidateMbid_DoesNotShortCircuit()
    {
        // Regression guard for MATCH-01: the OLD code compared localTrack.Id (a path
        // hash) to the candidate MBID. A hash can never legitimately equal an MBID, so
        // this must NOT produce a perfect score — the exact-ID path requires real
        // tag-derived localIds.
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();
        var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);

        var localTrack = new Track("mbid-target-123", "Fuzzy Title", "ar9", "Wrong Artist", "al9", "Wrong Album", 111.0, "C:/nonexistent/file.mp3", "Local", 0, 0, DateTime.UtcNow);

        var candidate = new ExternalTrackMetadata(
            "Fuzzy Title", "Wrong Artist", "Wrong Album", 0, null, 0, null, 111.0, null,
            new ExternalIds(MusicBrainzId: "mbid-target-123"));

        var result = matcher.ScoreCandidate(localTrack, candidate, "MusicBrainz", candidate.ExternalIds, localIds: null);

        Assert.NotEqual(1.0, result.Confidence);
        Assert.False(result.IsExactIdMatch);
        Assert.DoesNotContain("Exact MusicBrainz ID match", result.MatchEvidence);
        Assert.DoesNotContain("Exact ISRC match", result.MatchEvidence);
    }

    [Fact]
    public async Task FindMatchesForTrackAsync_LocalFileTagsWithMbid_RankExactIdCandidateFirst()
    {
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();

        string tempDir = Path.Combine(Path.GetTempPath(), "Octave_MatcherTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string taggedFile = Path.Combine(tempDir, "tagged.mp3");
        try
        {
            File.WriteAllBytes(taggedFile, ValidMp3Bytes);
            using (var tagFile = TagLib.File.Create(taggedFile))
            {
                tagFile.Tag.MusicBrainzTrackId = "mbid-e2e-1";
                tagFile.Save();
            }

            // Local fuzzy metadata is completely wrong; only the file's MBID is truth.
            var localTrack = new Track(
                "hash_of_path", "Qwerty Wrong Title", "ar9", "Zzzz Wrong Artist",
                "al9", "Wrong Album", 123.0, taggedFile, "Local", 0, 0, DateTime.UtcNow);

            var garbageButRightId = new TrackMatchCandidate(
                "MusicBrainz", new ExternalIds("mbid-e2e-1"), 0.5, "Initial",
                new ExternalTrackMetadata("Completely Different Song", "Other Artist", "Other Album", 1999, null, 1, 1, 999.0, null, new ExternalIds("mbid-e2e-1")));

            var perfectFuzzyNoId = new TrackMatchCandidate(
                "MusicBrainz", new ExternalIds("mbid-other"), 0.5, "Initial",
                new ExternalTrackMetadata("Qwerty Wrong Title", "Zzzz Wrong Artist", "Wrong Album", 2020, null, 1, 1, 123.0, null, new ExternalIds("mbid-other")));

            mockOrchestrator.Setup(o => o.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { perfectFuzzyNoId, garbageButRightId });

            var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);
            var ranked = await matcher.FindMatchesForTrackAsync(localTrack);

            Assert.Equal(2, ranked.Count);
            Assert.Equal("mbid-e2e-1", ranked[0].ExternalIds.MusicBrainzId);
            Assert.Equal(1.0, ranked[0].Confidence);
            Assert.Contains("Exact MusicBrainz ID match", ranked[0].MatchEvidence);
            Assert.True(ranked[0].Confidence > ranked[1].Confidence);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // =================================================================
    // 11. BATCH 7 — DURATION TIER PLACEMENT (MATCH-03) & DEAD ZONE (MATCH-06)
    // =================================================================

    // Baseline where title/artist/album/track all match exactly: 0.35 + 0.30 +
    // 0.10 + 0.05 = 0.80 before the duration component, so each duration tier
    // moves the confidence in isolation.
    private static readonly Track ScoringBaselineTrack =
        new("tr1", "Signal", "ar1", "Artist X", "al1", "Album Y", 300.0, "C:/music/signal.mp3", "Local", 3, 2020, DateTime.UtcNow);

    private static ExternalTrackMetadata CandidateWithDuration(double? durationSeconds) =>
        new("Signal", "Artist X", "Album Y", 2020, "Rock", 3, 1, durationSeconds, null, ExternalIds.Empty);

    [Fact]
    public void ScoreCandidate_MissingDuration_RanksBelowCloseAndAboveFarDurations()
    {
        var matcher = new TrackMetadataMatcher(new Mock<IExternalMetadataOrchestrator>().Object);

        double exactConfidence = matcher.ScoreCandidate(ScoringBaselineTrack, CandidateWithDuration(300.0), "MB").Confidence;
        double closeConfidence = matcher.ScoreCandidate(ScoringBaselineTrack, CandidateWithDuration(306.0), "MB").Confidence; // ±6s → +0.08
        double missingConfidence = matcher.ScoreCandidate(ScoringBaselineTrack, CandidateWithDuration(null), "MB").Confidence;
        double farConfidence = matcher.ScoreCandidate(ScoringBaselineTrack, CandidateWithDuration(312.0), "MB").Confidence;   // ±12s → +0.02

        // MATCH-03: absent length must not outscore present-but-imperfect data.
        // Neutral +0.05 places unknown between the ±8 s (+0.08) and ±15 s
        // (+0.02) tiers — the old +0.10 beat every imperfect-but-real value.
        Assert.True(exactConfidence > closeConfidence, $"exact {exactConfidence} should exceed ±6s-off {closeConfidence}");
        Assert.True(closeConfidence > missingConfidence, $"±6s-off {closeConfidence} should exceed missing {missingConfidence}");
        Assert.True(missingConfidence > farConfidence, $"missing {missingConfidence} should exceed ±12s-off {farConfidence}");
    }

    [Fact]
    public void ScoreCandidate_TwentySecondDurationDiff_IsPenalizedNotIgnored()
    {
        var matcher = new TrackMetadataMatcher(new Mock<IExternalMetadataOrchestrator>().Object);

        var tolerated = matcher.ScoreCandidate(ScoringBaselineTrack, CandidateWithDuration(315.0), "MB"); // ±15s → +0.02
        var penalized = matcher.ScoreCandidate(ScoringBaselineTrack, CandidateWithDuration(320.0), "MB"); // ±20s → penalty

        // MATCH-06: the old `diff > 25` tier left 15–25 s unscored — a ~20 s-off
        // different edit escaped with the same credit as a near miss. It is now
        // an explicit mismatch below the auto-apply gates.
        Assert.True(penalized.Confidence < tolerated.Confidence,
            $"±20s ({penalized.Confidence}) must score below ±15s ({tolerated.Confidence})");
        Assert.True(penalized.Confidence < 0.70, $"Expected mismatch penalty to pull confidence < 0.70, got {penalized.Confidence}");
        Assert.Contains("Mismatch", penalized.MatchEvidence);
    }

    // =================================================================
    // 12. TAG-READ FAILURE TRACK NUMBER FALLBACK (MATCH-07)
    // =================================================================

    // fLaC magic followed by an invalid metadata-block header — TagLib's FLAC
    // reader validates strictly and throws on open, simulating an unreadable
    // tag payload while the FILENAME still carries the real position.
    private static readonly byte[] UnreadableFlacBytes = { 0x66, 0x4C, 0x61, 0x43, 0xFF, 0x00, 0x00 };

    [Fact]
    public async Task FindMatchesForFileAsync_UnreadableTags_TrackNumberComesFromFilename()
    {
        var mockOrchestrator = new Mock<IExternalMetadataOrchestrator>();
        string tempDir = Path.Combine(Path.GetTempPath(), "Octave_Match7_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "07 - Pink Floyd - Time.flac");
            File.WriteAllBytes(filePath, UnreadableFlacBytes);

            mockOrchestrator.Setup(o => o.SearchTrackCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[]
                {
                    new TrackMatchCandidate(
                        "MusicBrainz", new ExternalIds("mb-time"), 0.5, "Initial",
                        new ExternalTrackMetadata("Time", "Pink Floyd", Path.GetFileName(tempDir), 1973, "Rock", 7, 1, null, null, new ExternalIds("mb-time")))
                });

            var matcher = new TrackMetadataMatcher(mockOrchestrator.Object);
            var ranked = await matcher.FindMatchesForFileAsync(filePath);

            Assert.Single(ranked);

            // MATCH-07: the old default of trackNum=1 survived the TagLib failure,
            // so the filename fallback (`trackNum <= 0`) never fired and the #7
            // bonus was silently lost. The evidence line only appears when the
            // transient track actually carried TrackNumber 7.
            Assert.Contains("Track #7 Match", ranked[0].MatchEvidence);
            Assert.True(ranked[0].Confidence >= 0.85,
                $"Expected >= 0.85 with the track-number bonus applied, got {ranked[0].Confidence}");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
