using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Octave.Core.Models;

using Octave.Core.Interfaces;

namespace Octave.Core.Services.Metadata;

/// <summary>
/// Intelligent fuzzy metadata matcher for lyrics provider search candidates.
/// Evaluates and scores search candidates based on title similarity, artist set overlap,
/// duration proximity, and weak album correlation.
/// </summary>
public class LyricsMatcher : ILyricsMatcher
{
    public const double DefaultConfidenceThreshold = 0.60;

    private static readonly Regex SoundtrackAttributionRegex = new(
        @"\s*[\(\[]\s*(?:from|ost|theme\s+from|taken\s+from|featured\s+in)\s+[""'\u201C\u201D\u2018\u2019]?.*?[""'\u201C\u201D\u2018\u2019]?\s*[\)\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FeaturedArtistRegex = new(
        @"\s*[\(\[]\s*(?:feat\.|featuring|ft\.)\s+.*?[\)\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EditionTagsRegex = new(
        @"\s*[\(\[]\s*(?:official\s+(?:video|audio|music\s+video)|remaster(?:ed)?(?:\s+\d{4})?|deluxe(?:\s+edition)?|anniversary(?:\s+edition)?|bonus\s+track|album\s+version|single\s+version|original\s+motion\s+picture\s+soundtrack|original\s+soundtrack)\s*[\)\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TrailingHyphenAttributionRegex = new(
        @"\s*-\s*(?:from|remastered|bonus\s+track|single\s+version|live).*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // MATCH-01: deliberately does NOT split on "and" — multi-word band names like
    // "Florence and the Machine" would otherwise be shredded into fragments, corrupting
    // the derived primary artist and the overlap scoring. Real separators only.
    private static readonly Regex ArtistSeparatorsRegex = new(
        @"\s*(?:;|,|\/|&|\||\+|\bfeat\.?\b|\bfeaturing\b|\bft\.?\b)\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PunctuationCleanRegex = new(
        @"[^\p{L}\p{Nd}\s]",
        RegexOptions.Compiled);

    private static readonly Regex MultipleSpacesRegex = new(
        @"\s+",
        RegexOptions.Compiled);

    /// <summary>
    /// Normalizes a track title by safely stripping soundtrack attributions, featured artists,
    /// edition/remaster tags, punctuation, and extra whitespace for fuzzy comparison.
    /// </summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        string result = title.Trim();

        // 1. Strip soundtrack tags like (From "Yeh Jawaani Hai Deewani")
        result = SoundtrackAttributionRegex.Replace(result, " ");

        // 2. Strip featured artist tags like (feat. Artist)
        result = FeaturedArtistRegex.Replace(result, " ");

        // 3. Strip edition tags like (Remastered 2020)
        result = EditionTagsRegex.Replace(result, " ");

        // 4. Strip trailing hyphen tags like - From "Movie" or - Single Version
        result = TrailingHyphenAttributionRegex.Replace(result, " ");

        // 5. Clean punctuation and collapse spaces
        result = PunctuationCleanRegex.Replace(result, " ");
        result = MultipleSpacesRegex.Replace(result, " ").Trim();

        return result.ToLowerInvariant();
    }

    /// <summary>
    /// Parses an artist string into a normalized, deduplicated list of individual artist
    /// names preserving first-seen order. Order matters: the first-listed artist is the
    /// "primary" artist used for LRCLIB lookups, so callers must be able to rely on it.
    /// </summary>
    public static IReadOnlyList<string> ParseArtistSet(string? artistString)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        if (string.IsNullOrWhiteSpace(artistString)) return ordered;

        string[] parts = ArtistSeparatorsRegex.Split(artistString);
        foreach (string part in parts)
        {
            string cleaned = PunctuationCleanRegex.Replace(part, " ");
            cleaned = MultipleSpacesRegex.Replace(cleaned, " ").Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Length > 1 && seen.Add(cleaned))
            {
                ordered.Add(cleaned);
            }
        }

        return ordered;
    }

    /// <summary>
    /// Calculates similarity between two normalized titles (0.0 to 1.0).
    /// </summary>
    public static double CalculateTitleSimilarity(string? title1, string? title2)
    {
        string norm1 = NormalizeTitle(title1);
        string norm2 = NormalizeTitle(title2);

        if (string.IsNullOrEmpty(norm1) && string.IsNullOrEmpty(norm2)) return 1.0;
        if (string.IsNullOrEmpty(norm1) || string.IsNullOrEmpty(norm2)) return 0.0;

        if (string.Equals(norm1, norm2, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        // Substring / prefix match
        if (norm1.Contains(norm2, StringComparison.OrdinalIgnoreCase) || norm2.Contains(norm1, StringComparison.OrdinalIgnoreCase))
        {
            double minLen = Math.Min(norm1.Length, norm2.Length);
            double maxLen = Math.Max(norm1.Length, norm2.Length);
            return 0.85 + 0.15 * (minLen / maxLen);
        }

        // Word token similarity (Dice coefficient on words)
        var words1 = norm1.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var words2 = norm2.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (words1.Count == 0 || words2.Count == 0) return 0.0;

        int intersect = words1.Intersect(words2, StringComparer.OrdinalIgnoreCase).Count();
        return (2.0 * intersect) / (words1.Count + words2.Count);
    }

    /// <summary>
    /// Calculates overlap ratio between artist lists (0.0 to 1.0). The lists are
    /// order-preserving (from <see cref="ParseArtistSet"/>); set semantics are derived
    /// internally so the primary/first-listed artist is what callers rely on elsewhere.
    /// </summary>
    public static double CalculateArtistOverlap(IReadOnlyList<string> set1, IReadOnlyList<string> set2, string? raw1, string? raw2)
    {
        if ((set1 == null || set1.Count == 0) && (set2 == null || set2.Count == 0)) return 0.5;
        if (set1 == null || set1.Count == 0 || set2 == null || set2.Count == 0)
        {
            // Fallback to substring check on raw names
            if (!string.IsNullOrWhiteSpace(raw1) && !string.IsNullOrWhiteSpace(raw2))
            {
                string c1 = PunctuationCleanRegex.Replace(raw1, "").ToLowerInvariant();
                string c2 = PunctuationCleanRegex.Replace(raw2, "").ToLowerInvariant();
                if (c1.Contains(c2) || c2.Contains(c1)) return 0.8;
            }
            return 0.3;
        }

        var hash1 = set1.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hash2 = set2.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (hash1.SetEquals(hash2))
        {
            return 1.0;
        }

        int matches = 0;
        foreach (var a1 in hash1)
        {
            if (hash2.Contains(a1))
            {
                matches++;
            }
            else
            {
                // Partial token match (e.g. "Arijit Singh" vs "Arijit")
                if (hash2.Any(a2 => a1.Contains(a2) || a2.Contains(a1)))
                {
                    matches++;
                }
            }
        }

        if (matches == 0)
        {
            return 0.0;
        }

        // Ratio of matched artists relative to smaller set (so multi-artist collabs match single primary artist)
        double overlap = (double)matches / Math.Min(set1.Count, set2.Count);
        return Math.Min(1.0, overlap);
    }

    /// <summary>
    /// Calculates duration similarity score (0.0 to 1.0).
    /// </summary>
    public static double CalculateDurationScore(double? targetDuration, double? candidateDuration)
    {
        if (!targetDuration.HasValue || targetDuration.Value <= 0 ||
            !candidateDuration.HasValue || candidateDuration.Value <= 0)
        {
            return 0.5; // neutral when duration unknown
        }

        double diff = Math.Abs(targetDuration.Value - candidateDuration.Value);

        if (diff <= 1.0) return 1.0;
        if (diff <= 2.0) return 0.95;
        if (diff <= 5.0) return 0.80;
        if (diff <= 10.0) return 0.50;
        if (diff <= 20.0) return 0.20;
        if (diff <= 30.0) return 0.05;
        return 0.0;
    }

    /// <summary>
    /// Calculates album similarity score (0.0 to 1.0). Album mismatch is a weak signal, not a hard rejection.
    /// </summary>
    public static double CalculateAlbumScore(string? album1, string? album2)
    {
        if (string.IsNullOrWhiteSpace(album1) || string.IsNullOrWhiteSpace(album2))
        {
            return 0.3; // neutral
        }

        string norm1 = NormalizeTitle(album1);
        string norm2 = NormalizeTitle(album2);

        if (string.Equals(norm1, norm2, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        if (norm1.Contains(norm2, StringComparison.OrdinalIgnoreCase) || norm2.Contains(norm1, StringComparison.OrdinalIgnoreCase))
        {
            return 0.7;
        }

        return 0.3; // weak penalty, not 0
    }

    /// <summary>
    /// Computes overall candidate match score (0.0 to 1.0).
    /// </summary>
    public static double ScoreCandidate(
        string trackTitle,
        string artistName,
        string? albumName,
        double? durationSeconds,
        LrclibResponse candidate)
    {
        if (candidate == null) return 0.0;

        string candidateTitle = candidate.TrackName ?? candidate.Name ?? string.Empty;
        string candidateArtist = candidate.ArtistName ?? string.Empty;
        string? candidateAlbum = candidate.AlbumName;
        double? candidateDuration = candidate.Duration;

        double titleScore = CalculateTitleSimilarity(trackTitle, candidateTitle);
        if (titleScore < 0.35)
        {
            return 0.0; // Title is completely different track
        }

        var trackArtists = ParseArtistSet(artistName);
        var candidateArtists = ParseArtistSet(candidateArtist);
        double artistScore = CalculateArtistOverlap(trackArtists, candidateArtists, artistName, candidateArtist);

        // If both title and artist are weak, reject
        if (titleScore < 0.5 && artistScore < 0.4)
        {
            return 0.0;
        }

        double durationScore = CalculateDurationScore(durationSeconds, candidateDuration);
        double albumScore = CalculateAlbumScore(albumName, candidateAlbum);

        // Weighted combination
        const double TitleWeight = 0.45;
        const double ArtistWeight = 0.30;
        const double DurationWeight = 0.20;
        const double AlbumWeight = 0.05;

        double totalScore = (TitleWeight * titleScore) +
                            (ArtistWeight * artistScore) +
                            (DurationWeight * durationScore) +
                            (AlbumWeight * albumScore);

        return Math.Round(totalScore, 4);
    }

    /// <summary>
    /// Evaluates all candidate responses from LRCLIB and returns the best matching candidate
    /// that exceeds the confidence threshold.
    /// </summary>
    public static (LrclibResponse? Candidate, double Score) SelectBestCandidate(
        string trackTitle,
        string artistName,
        string? albumName,
        double? durationSeconds,
        IEnumerable<LrclibResponse>? candidates,
        double minScore = DefaultConfidenceThreshold)
    {
        if (candidates == null) return (null, 0.0);

        var scored = new List<(LrclibResponse Candidate, double Score, double DurationDiff)>();

        foreach (var candidate in candidates)
        {
            double score = ScoreCandidate(trackTitle, artistName, albumName, durationSeconds, candidate);
            if (score >= minScore)
            {
                double durationDiff = (durationSeconds.HasValue && candidate.Duration.HasValue)
                    ? Math.Abs(durationSeconds.Value - candidate.Duration.Value)
                    : 999.0;

                scored.Add((candidate, score, durationDiff));
            }
        }

        if (scored.Count == 0)
        {
            return (null, 0.0);
        }

        // Sort by Score descending, then by closest duration ascending
        var best = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.DurationDiff)
            .First();

        return (best.Candidate, best.Score);
    }

    public string NormalizeTitleInstance(string? title) => NormalizeTitle(title);
    public IReadOnlyList<string> ParseArtistSetInstance(string? artist) => ParseArtistSet(artist);

    string ILyricsMatcher.NormalizeTitle(string? title) => NormalizeTitle(title);
    IReadOnlyList<string> ILyricsMatcher.ParseArtistSet(string? artist) => ParseArtistSet(artist);
    (LrclibResponse? BestCandidate, double Score) ILyricsMatcher.SelectBestCandidate(
        string targetTitle,
        string targetArtist,
        string? targetAlbum,
        double targetDurationSeconds,
        IReadOnlyList<LrclibResponse> candidates,
        double minScore) => SelectBestCandidate(targetTitle, targetArtist, targetAlbum, targetDurationSeconds, candidates, minScore);
}

