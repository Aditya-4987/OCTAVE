using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Octave.Core.Interfaces;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Database;
using Octave.Core.Services.External;
using Octave.Core.Services.Library;
using Octave.Core.Services.Metadata;
using Octave.Core.Services.Network;
using Xunit;

namespace Octave.Core.Tests;

public class TrackEnrichmentWorkflowTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDbContext _dbContext;

    private static readonly byte[] ValidMp3Bytes = new byte[] {
        0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    private static readonly byte[] SampleJpegBytes = new byte[] {
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x01, 0x00, 0x60, 0x00, 0x60, 0x00, 0x00, 0xFF, 0xD9
    };

    public TrackEnrichmentWorkflowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_EnrichTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        string dbPath = Path.Combine(_tempDir, "test.db");
        _dbContext = new SqliteDbContext($"Data Source={dbPath}");
        _dbContext.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private async Task<(string FilePath, Track Track)> CreateTestTrackAsync(string fileName = "enrich_test.mp3")
    {
        string filePath = Path.Combine(_tempDir, fileName);
        await File.WriteAllBytesAsync(filePath, ValidMp3Bytes);

        using (var tagFile = TagLib.File.Create(filePath))
        {
            tagFile.Tag.Title = "Original Title";
            tagFile.Tag.Performers = new[] { "Original Artist" };
            tagFile.Tag.Album = "Original Album";
            tagFile.Tag.Year = 1999;
            tagFile.Tag.Track = 1;
            tagFile.Tag.Genres = new[] { "Pop" };
            tagFile.Save();
        }

        var track = new Track(
            "tr_enrich_1",
            "Original Title",
            "ar_orig",
            "Original Artist",
            "al_orig",
            "Original Album",
            240.0,
            filePath,
            "Local",
            1,
            1999,
            DateTime.UtcNow,
            "Pop",
            0.0f);

        await _dbContext.UpsertTrackAsync(track);
        return (filePath, track);
    }

    // =================================================================
    // 1. SUCCESSFUL FULL APPLICATION (All proposed fields applied)
    // =================================================================

    [Fact]
    public async Task ApplyEnrichmentAsync_FullApplication_UpdatesAllFieldsAndRefreshesDb()
    {
        var (filePath, track) = await CreateTestTrackAsync("full_apply.mp3");

        var mockMatcher = new Mock<ITrackMetadataMatcher>();
        var mockLibraryService = new Mock<ILibraryService>();
        var artworkCache = new ArtworkCacheManager(_tempDir);
        var metadataEditor = new TrackMetadataEditor(_dbContext, mockLibraryService.Object, artworkCache);

        var candidateMeta = new ExternalTrackMetadata(
            "Enriched Title",
            "Enriched Artist",
            "Enriched Album",
            2024,
            "Progressive Rock",
            7,
            1,
            240.0,
            "GBUM71029603",
            new ExternalIds("mb-rec-111", AdditionalIds: new Dictionary<string, string> { ["MusicBrainzReleaseId"] = "mb-rel-222" }));

        var candidateMatch = new TrackMatchCandidate("MusicBrainz", candidateMeta.ExternalIds, 0.98, "High match", candidateMeta);
        mockMatcher.Setup(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candidateMatch });

        var workflow = new TrackEnrichmentWorkflow(_dbContext, mockMatcher.Object, metadataEditor);

        // 1. Generate plan
        var plan = await workflow.CreateEnrichmentPlanAsync(track);

        Assert.Equal(MatchConfidenceTier.ExactMatch, plan.TopConfidenceTier);
        Assert.NotNull(plan.BestCandidate);
        Assert.True(plan.BestCandidate.Title.HasDifference);
        Assert.Equal("Enriched Title", plan.BestCandidate.Title.ProposedValue);
        Assert.Equal("Original Title", plan.BestCandidate.Title.CurrentValue);

        // 2. Apply all fields
        var applyReq = new EnrichmentApplyRequest(track.Id, plan.BestCandidate, new FieldSelectionOptions());
        var result = await workflow.ApplyEnrichmentAsync(applyReq);

        Assert.True(result.Success);
        Assert.Contains("Title", result.AppliedFields);
        Assert.Contains("Artist", result.AppliedFields);
        Assert.Contains("Album", result.AppliedFields);
        Assert.Contains("Year", result.AppliedFields);

        // 3. Verify file on disk
        using (var reread = TagLib.File.Create(filePath))
        {
            Assert.Equal("Enriched Title", reread.Tag.Title);
            Assert.Equal("Enriched Artist", reread.Tag.Performers[0]);
            Assert.Equal("Enriched Album", reread.Tag.Album);
            Assert.Equal((uint)2024, reread.Tag.Year);
            Assert.Equal((uint)7, reread.Tag.Track);
            Assert.Equal("Progressive Rock", reread.Tag.Genres[0]);
        }

        // 4. Verify DB was refreshed
        var dbTrack = await _dbContext.GetTrackByIdAsync(track.Id);
        Assert.NotNull(dbTrack);
        Assert.Equal("Enriched Title", dbTrack.Title);
        Assert.Equal("Enriched Artist", dbTrack.ArtistName);
        Assert.Equal(2024, dbTrack.Year);
    }

    // =================================================================
    // 2. APPLYING ONLY SELECTED FIELDS
    // =================================================================

    [Fact]
    public async Task ApplyEnrichmentAsync_SelectedFieldsOnly_PreservesUnselectedOriginalTags()
    {
        var (filePath, track) = await CreateTestTrackAsync("partial_apply.mp3");

        var mockMatcher = new Mock<ITrackMetadataMatcher>();
        var mockLibraryService = new Mock<ILibraryService>();
        var artworkCache = new ArtworkCacheManager(_tempDir);
        var metadataEditor = new TrackMetadataEditor(_dbContext, mockLibraryService.Object, artworkCache);

        var candidateMeta = new ExternalTrackMetadata(
            "New Online Title",
            "New Online Artist",
            "New Online Album",
            2025,
            "Pop",
            9,
            1,
            240.0,
            null,
            ExternalIds.Empty);

        var candidateMatch = new TrackMatchCandidate("MusicBrainz", ExternalIds.Empty, 0.90, "Matched", candidateMeta);
        mockMatcher.Setup(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candidateMatch });

        var workflow = new TrackEnrichmentWorkflow(_dbContext, mockMatcher.Object, metadataEditor);
        var plan = await workflow.CreateEnrichmentPlanAsync(track);

        // User selects ONLY Year and TrackNumber (unchecks Title, Artist, Album)
        var selection = new FieldSelectionOptions(
            ApplyTitle: false,
            ApplyArtist: false,
            ApplyAlbum: false,
            ApplyYear: true,
            ApplyTrackNumber: true);

        var applyReq = new EnrichmentApplyRequest(track.Id, plan.BestCandidate!, selection);
        var result = await workflow.ApplyEnrichmentAsync(applyReq);

        Assert.True(result.Success);
        Assert.Contains("Year", result.AppliedFields);
        Assert.Contains("TrackNumber", result.AppliedFields);
        Assert.DoesNotContain("Title", result.AppliedFields);
        Assert.DoesNotContain("Artist", result.AppliedFields);

        // Verify Title & Artist remained original on disk, but Year & Track changed
        using (var reread = TagLib.File.Create(filePath))
        {
            Assert.Equal("Original Title", reread.Tag.Title);
            Assert.Equal("Original Artist", reread.Tag.Performers[0]);
            Assert.Equal((uint)2025, reread.Tag.Year);
            Assert.Equal((uint)9, reread.Tag.Track);
        }
    }

    // =================================================================
    // 3. REJECTED CANDIDATE (No write performed)
    // =================================================================

    [Fact]
    public async Task CreateEnrichmentPlanAsync_RejectedCandidate_PerformsZeroWrites()
    {
        var (filePath, track) = await CreateTestTrackAsync("reject_test.mp3");

        var mockMatcher = new Mock<ITrackMetadataMatcher>();
        var mockLibraryService = new Mock<ILibraryService>();
        var metadataEditor = new TrackMetadataEditor(_dbContext, mockLibraryService.Object);

        var candidateMeta = new ExternalTrackMetadata("Different Song", "Different Artist", "Album", 2020, "Rock", 1, 1, 240, null, ExternalIds.Empty);
        var candidateMatch = new TrackMatchCandidate("MusicBrainz", ExternalIds.Empty, 0.40, "Weak match", candidateMeta);

        mockMatcher.Setup(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candidateMatch });

        var workflow = new TrackEnrichmentWorkflow(_dbContext, mockMatcher.Object, metadataEditor);

        // User generates plan but rejects it (does not call ApplyEnrichmentAsync)
        var plan = await workflow.CreateEnrichmentPlanAsync(track);

        Assert.Equal(MatchConfidenceTier.AmbiguousMatch, plan.TopConfidenceTier);

        // File and DB must remain 100% unchanged
        using (var reread = TagLib.File.Create(filePath))
        {
            Assert.Equal("Original Title", reread.Tag.Title);
            Assert.Equal("Original Artist", reread.Tag.Performers[0]);
        }

        var dbTrack = await _dbContext.GetTrackByIdAsync(track.Id);
        Assert.Equal("Original Title", dbTrack!.Title);
    }

    // =================================================================
    // 4. NO CANDIDATE
    // =================================================================

    [Fact]
    public async Task CreateEnrichmentPlanAsync_NoMatchesFound_ReturnsNoMatchTier()
    {
        var (_, track) = await CreateTestTrackAsync("no_match.mp3");

        var mockMatcher = new Mock<ITrackMetadataMatcher>();
        mockMatcher.Setup(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TrackMatchCandidate>());

        var mockLibraryService = new Mock<ILibraryService>();
        var metadataEditor = new TrackMetadataEditor(_dbContext, mockLibraryService.Object);

        var workflow = new TrackEnrichmentWorkflow(_dbContext, mockMatcher.Object, metadataEditor);
        var plan = await workflow.CreateEnrichmentPlanAsync(track);

        Assert.Equal(MatchConfidenceTier.NoMatch, plan.TopConfidenceTier);
        Assert.Empty(plan.Candidates);
        Assert.Null(plan.BestCandidate);
    }

    // =================================================================
    // 5. ARTWORK FAILURE DOES NOT BLOCK METADATA APPLICATION
    // =================================================================

    [Fact]
    public async Task ApplyEnrichmentAsync_ArtworkFailure_StillAppliesMetadataFieldsSuccessfully()
    {
        var (filePath, track) = await CreateTestTrackAsync("art_fail.mp3");

        var mockMatcher = new Mock<ITrackMetadataMatcher>();
        var mockLibraryService = new Mock<ILibraryService>();
        var artworkCache = new ArtworkCacheManager(_tempDir);
        var metadataEditor = new TrackMetadataEditor(_dbContext, mockLibraryService.Object, artworkCache);

        var candidateMeta = new ExternalTrackMetadata("Song With Bad Art", "Artist", "Album", 2024, "Rock", 1, 1, 240, null, ExternalIds.Empty);
        var candidateMatch = new TrackMatchCandidate("MusicBrainz", ExternalIds.Empty, 0.95, "Matched", candidateMeta);

        mockMatcher.Setup(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { candidateMatch });

        var workflow = new TrackEnrichmentWorkflow(_dbContext, mockMatcher.Object, metadataEditor);
        var plan = await workflow.CreateEnrichmentPlanAsync(track);

        // Candidate has no proposed artwork bytes (e.g. artwork download failed)
        Assert.Null(plan.BestCandidate!.ProposedArtworkBytes);

        var applyReq = new EnrichmentApplyRequest(track.Id, plan.BestCandidate, new FieldSelectionOptions(ApplyArtwork: true));
        var result = await workflow.ApplyEnrichmentAsync(applyReq);

        Assert.True(result.Success);
        Assert.Contains("Title", result.AppliedFields);

        // Verify tags written
        using (var reread = TagLib.File.Create(filePath))
        {
            Assert.Equal("Song With Bad Art", reread.Tag.Title);
        }
    }

    // =================================================================
    // 6. METADATA WRITE FAILURE (ReadOnly file prevents DB corruption)
    // =================================================================

    [Fact]
    public async Task ApplyEnrichmentAsync_WriteFailure_LeavesDatabaseUnchanged()
    {
        var (filePath, track) = await CreateTestTrackAsync("write_fail.mp3");
        File.SetAttributes(filePath, FileAttributes.ReadOnly);

        try
        {
            var mockMatcher = new Mock<ITrackMetadataMatcher>();
            var mockLibraryService = new Mock<ILibraryService>();
            var metadataEditor = new TrackMetadataEditor(_dbContext, mockLibraryService.Object);

            var candidateMeta = new ExternalTrackMetadata("Should Not Apply", "Artist", "Album", 2024, "Rock", 1, 1, 240, null, ExternalIds.Empty);
            var candidateMatch = new TrackMatchCandidate("MusicBrainz", ExternalIds.Empty, 0.95, "Matched", candidateMeta);

            mockMatcher.Setup(m => m.FindMatchesForTrackAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { candidateMatch });

            var workflow = new TrackEnrichmentWorkflow(_dbContext, mockMatcher.Object, metadataEditor);
            var plan = await workflow.CreateEnrichmentPlanAsync(track);

            var applyReq = new EnrichmentApplyRequest(track.Id, plan.BestCandidate!, new FieldSelectionOptions());
            var result = await workflow.ApplyEnrichmentAsync(applyReq);

            Assert.False(result.Success);

            // Verify database track title was NOT modified
            var dbTrack = await _dbContext.GetTrackByIdAsync(track.Id);
            Assert.Equal("Original Title", dbTrack!.Title);
        }
        finally
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
        }
    }
}
