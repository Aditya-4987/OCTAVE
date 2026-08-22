using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Helpers;
using Octave.Core.Models;

namespace Octave.Core.Services.External;

public class TrackMetadataMatcher : ITrackMetadataMatcher
{
    private readonly IExternalMetadataOrchestrator _metadataOrchestrator;

    public TrackMetadataMatcher(IExternalMetadataOrchestrator metadataOrchestrator)
    {
        _metadataOrchestrator = metadataOrchestrator ?? throw new ArgumentNullException(nameof(metadataOrchestrator));
    }

    public async Task<IReadOnlyList<TrackMatchCandidate>> FindMatchesForTrackAsync(
        Track track,
        CancellationToken ct = default)
    {
        if (track == null) return Array.Empty<TrackMatchCandidate>();

        string searchTitle = track.Title;
        string searchArtist = track.ArtistName;
        string? searchAlbum = track.AlbumTitle;
        double? searchDuration = track.DurationSeconds > 0 ? track.DurationSeconds : null;

        // Fallback to filename parsing if title or artist is generic/missing
        if (IsMissingOrGeneric(searchTitle) || IsMissingOrGeneric(searchArtist))
        {
            if (!string.IsNullOrWhiteSpace(track.SourceUri) && File.Exists(track.SourceUri))
            {
                var (fileTitle, fileArtist, fileAlbum, _) = MetadataTextNormalizer.ParseFromPath(track.SourceUri);
                if (!string.IsNullOrWhiteSpace(fileTitle)) searchTitle = fileTitle;
                if (!string.IsNullOrWhiteSpace(fileArtist)) searchArtist = fileArtist;
                if (!string.IsNullOrWhiteSpace(fileAlbum) && string.IsNullOrWhiteSpace(searchAlbum)) searchAlbum = fileAlbum;
            }
        }

        if (string.IsNullOrWhiteSpace(searchTitle) || string.IsNullOrWhiteSpace(searchArtist))
        {
            return Array.Empty<TrackMatchCandidate>();
        }

        // Query external providers via orchestrator
        var initialCandidates = await _metadataOrchestrator.SearchTrackCandidatesAsync(
            searchTitle,
            searchArtist,
            searchAlbum,
            searchDuration,
            ct).ConfigureAwait(false);

        if (initialCandidates == null || initialCandidates.Count == 0)
        {
            return Array.Empty<TrackMatchCandidate>();
        }

        // MATCH-01: the local track's real identifiers live in its file tags, not in
        // Track.Id (a path hash). Read them once so the exact-ID short-circuit in
        // ScoreCandidate can actually fire; null for remote/unreadable sources.
        ExternalIds? localIds = ExternalTagIds.TryReadFromFile(track.SourceUri);

        // Re-score every candidate using the deterministic scoring engine
        var scoredList = new List<TrackMatchCandidate>();
        foreach (var c in initialCandidates)
        {
            var scoreResult = ScoreCandidate(track, c.Metadata, c.ProviderName, c.ExternalIds, localIds);

            scoredList.Add(new TrackMatchCandidate(
                c.ProviderName,
                c.ExternalIds,
                scoreResult.Confidence,
                scoreResult.MatchEvidence,
                c.Metadata));
        }

        return scoredList.OrderByDescending(c => c.Confidence).ToList();
    }

    public async Task<IReadOnlyList<TrackMatchCandidate>> FindMatchesForFileAsync(
        string filePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return Array.Empty<TrackMatchCandidate>();
        }

        string title = string.Empty;
        string artist = string.Empty;
        string album = string.Empty;
        int trackNum = 1;
        int year = 0;
        double duration = 0;

        try
        {
            using (var tagFile = TagLib.File.Create(filePath))
            {
                title = tagFile.Tag.Title ?? string.Empty;
                artist = tagFile.Tag.Performers?.FirstOrDefault() ?? tagFile.Tag.AlbumArtists?.FirstOrDefault() ?? string.Empty;
                album = tagFile.Tag.Album ?? string.Empty;
                trackNum = (int)tagFile.Tag.Track;
                year = (int)tagFile.Tag.Year;
                duration = tagFile.Properties?.Duration.TotalSeconds ?? 0;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrackMetadataMatcher] Tag read error for '{filePath}': {ex.Message}");
        }

        // Fallback to filename parsing if tags were empty
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            var (fileTitle, fileArtist, fileAlbum, fileTrackNum) = MetadataTextNormalizer.ParseFromPath(filePath);
            if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(fileTitle)) title = fileTitle;
            if (string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(fileArtist)) artist = fileArtist;
            if (string.IsNullOrWhiteSpace(album) && !string.IsNullOrWhiteSpace(fileAlbum)) album = fileAlbum;
            if (trackNum <= 0 && fileTrackNum.HasValue) trackNum = fileTrackNum.Value;
        }

        string trackId = IdGenerator.FromTrackUri(filePath);
        string artistId = IdGenerator.FromArtist(!string.IsNullOrWhiteSpace(artist) ? artist : "Unknown Artist");
        string albumId = IdGenerator.FromAlbum(!string.IsNullOrWhiteSpace(artist) ? artist : "Unknown Artist", !string.IsNullOrWhiteSpace(album) ? album : "Unknown Album");

        var transientTrack = new Track(
            trackId,
            title,
            artistId,
            artist,
            albumId,
            album,
            duration,
            filePath,
            "Local",
            trackNum,
            year,
            DateTime.UtcNow);

        return await FindMatchesForTrackAsync(transientTrack, ct).ConfigureAwait(false);
    }

    public MatchScoreResult ScoreCandidate(
        Track localTrack,
        ExternalTrackMetadata candidate,
        string providerName,
        ExternalIds? candidateIds = null,
        ExternalIds? localIds = null)
    {
        if (candidate == null)
            return new MatchScoreResult(0.0, "Null candidate", false, false);

        var evidenceParts = new List<string>();

        // 1. Exact External ID Match (MBID / ISRC) — compares the LOCAL FILE's stored
        //    identifiers against the candidate's. (MATCH-01: this used to compare the
        //    path-hash localTrack.Id, which can never equal an MBID/ISRC, so the branch
        //    was dead code and genuine ID-exact matches were demoted to fuzzy scores.)
        if (candidateIds != null && localIds != null)
        {
            if (IsExactIdMatch(localIds.Isrc, candidateIds.Isrc))
            {
                return new MatchScoreResult(1.0, "Exact ISRC match", true, false);
            }

            if (IsExactIdMatch(localIds.MusicBrainzId, candidateIds.MusicBrainzId))
            {
                return new MatchScoreResult(1.0, "Exact MusicBrainz ID match", true, false);
            }
        }

        double score = 0.0;

        // 2. Title Similarity (Weight: 0.35)
        double titleSim = MetadataTextNormalizer.CalculateSimilarity(localTrack.Title, candidate.Title);
        score += titleSim * 0.35;
        if (titleSim >= 0.95)
            evidenceParts.Add("Exact Title Match (0.35)");
        else if (titleSim >= 0.70)
            evidenceParts.Add($"Fuzzy Title Match ({(titleSim * 0.35):0.##})");

        // 3. Artist Similarity (Weight: 0.30)
        double artistSim = MetadataTextNormalizer.CalculateSimilarity(localTrack.ArtistName, candidate.ArtistName);
        score += artistSim * 0.30;
        if (artistSim >= 0.95)
            evidenceParts.Add("Exact Artist Match (0.30)");
        else if (artistSim >= 0.70)
            evidenceParts.Add($"Fuzzy Artist Match ({(artistSim * 0.30):0.##})");

        // 4. Duration Proximity (Weight: 0.20)
        if (localTrack.DurationSeconds > 0 && candidate.DurationSeconds.HasValue && candidate.DurationSeconds.Value > 0)
        {
            double diff = Math.Abs(localTrack.DurationSeconds - candidate.DurationSeconds.Value);
            if (diff <= 1.5)
            {
                score += 0.20;
                evidenceParts.Add($"Exact Duration ±{diff:0.#}s (0.20)");
            }
            else if (diff <= 4.0)
            {
                score += 0.15;
                evidenceParts.Add($"Close Duration ±{diff:0.#}s (0.15)");
            }
            else if (diff <= 8.0)
            {
                score += 0.08;
                evidenceParts.Add($"Approximate Duration ±{diff:0.#}s (0.08)");
            }
            else if (diff <= 15.0)
            {
                score += 0.02;
                evidenceParts.Add($"Duration Diff ±{diff:0.#}s (0.02)");
            }
            else if (diff > 25.0)
            {
                score -= 0.15; // Duration mismatch penalty
                evidenceParts.Add($"Duration Mismatch -{diff:0.#}s (-0.15 penalty)");
            }
        }
        else
        {
            // Neutral duration signal
            score += 0.10;
        }

        // 5. Album Similarity (Weight: 0.10)
        if (!string.IsNullOrWhiteSpace(localTrack.AlbumTitle) && !string.IsNullOrWhiteSpace(candidate.AlbumTitle))
        {
            double albumSim = MetadataTextNormalizer.CalculateSimilarity(localTrack.AlbumTitle, candidate.AlbumTitle);
            score += albumSim * 0.10;
            if (albumSim >= 0.85)
                evidenceParts.Add("Album Match (0.10)");
        }

        // 6. Track Number Match (Weight: 0.05)
        if (localTrack.TrackNumber > 0 && candidate.TrackNumber.HasValue && candidate.TrackNumber.Value == localTrack.TrackNumber)
        {
            score += 0.05;
            evidenceParts.Add($"Track #{candidate.TrackNumber.Value} Match (0.05)");
        }

        // 7. Version & Modifier Compatibility
        var localVersion = MetadataTextNormalizer.ExtractVersionInfo(localTrack.Title, localTrack.AlbumTitle);
        var candidateVersion = MetadataTextNormalizer.ExtractVersionInfo(candidate.Title, candidate.AlbumTitle);

        bool isVersionMismatch = false;

        if (localVersion.IsLive != candidateVersion.IsLive)
        {
            isVersionMismatch = true;
            score -= 0.35;
            evidenceParts.Add(localVersion.IsLive ? "Version Mismatch: Local is Live, Candidate is Studio (-0.35)" : "Version Mismatch: Local is Studio, Candidate is Live (-0.35)");
        }
        else if (localVersion.IsRemix != candidateVersion.IsRemix)
        {
            isVersionMismatch = true;
            score -= 0.30;
            evidenceParts.Add("Version Mismatch: Remix vs Original (-0.30)");
        }
        else if (localVersion.IsInstrumental != candidateVersion.IsInstrumental)
        {
            isVersionMismatch = true;
            score -= 0.30;
            evidenceParts.Add("Version Mismatch: Instrumental vs Vocal (-0.30)");
        }
        else if (localVersion.IsAcoustic != candidateVersion.IsAcoustic)
        {
            isVersionMismatch = true;
            score -= 0.30;
            evidenceParts.Add("Version Mismatch: Acoustic vs Studio (-0.30)");
        }
        else if (localVersion.SpecificModifier != null && localVersion.SpecificModifier == candidateVersion.SpecificModifier)
        {
            score += 0.05;
            evidenceParts.Add($"Version Match: {localVersion.SpecificModifier} (+0.05)");
        }

        double finalConfidence = Math.Round(Math.Clamp(score, 0.0, 1.0), 2);
        string evidence = string.Join(", ", evidenceParts);

        return new MatchScoreResult(finalConfidence, evidence, false, isVersionMismatch);
    }

    private static bool IsMissingOrGeneric(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        string t = text.Trim().ToLowerInvariant();
        return t == "unknown artist" || t == "unknown" || t == "track" || t.StartsWith("track ");
    }

    private static bool IsExactIdMatch(string? localId, string? candidateId) =>
        !string.IsNullOrWhiteSpace(localId) &&
        !string.IsNullOrWhiteSpace(candidateId) &&
        localId.Trim().Equals(candidateId.Trim(), StringComparison.OrdinalIgnoreCase);
}
