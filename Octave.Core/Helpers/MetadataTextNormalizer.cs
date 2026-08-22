using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Octave.Core.Helpers;

public record VersionInfo(
    bool IsLive,
    bool IsRemix,
    bool IsAcoustic,
    bool IsInstrumental,
    bool IsDemo,
    bool IsRemaster,
    string? SpecificModifier
);

public static class MetadataTextNormalizer
{
    private static readonly Regex PunctuationRegex = new(@"[^\w\s]", RegexOptions.Compiled);
    private static readonly Regex MultipleSpacesRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex LiveRegex = new(@"\b(live|in concert|at the \w+|festival|tour)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RemixRegex = new(@"\b(remix|rmx|club mix|extended mix|radio edit|dub mix|vip mix|edit)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AcousticRegex = new(@"\b(acoustic|unplugged|piano version|orchestral)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InstrumentalRegex = new(@"\b(instrumental|karaoke|backing track)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DemoRegex = new(@"\b(demo|work in progress|rough mix|early take)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RemasterRegex = new(@"\b(remaster|remastered|digital remaster)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Strips diacritics/accents, converts typography to standard ASCII, removes punctuation, and collapses whitespace.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // 1. Remove diacritics (e.g. Björk -> Bjork, Café -> Cafe)
        string normalizedFormD = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalizedFormD.Length);

        foreach (char c in normalizedFormD)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        string cleanText = sb.ToString().Normalize(NormalizationForm.FormC);

        // 2. Normalize smart quotes, dashes, and symbols
        cleanText = cleanText
            .Replace("’", "'")
            .Replace("‘", "'")
            .Replace("`", "'")
            .Replace("´", "'")
            .Replace("“", "\"")
            .Replace("”", "\"")
            .Replace("—", "-")
            .Replace("–", "-")
            .Replace("&", "and");

        // 3. Remove punctuation and lowercase
        cleanText = PunctuationRegex.Replace(cleanText, " ");
        cleanText = MultipleSpacesRegex.Replace(cleanText, " ").Trim().ToLowerInvariant();

        return cleanText;
    }

    /// <summary>
    /// Analyzes title and album for version identifiers (Live, Remix, Acoustic, Instrumental, Demo, Remaster).
    /// </summary>
    public static VersionInfo ExtractVersionInfo(string? title, string? album = null)
    {
        string combined = $"{title ?? ""} {album ?? ""}";

        bool isLive = LiveRegex.IsMatch(combined);
        bool isRemix = RemixRegex.IsMatch(combined);
        bool isAcoustic = AcousticRegex.IsMatch(combined);
        bool isInstrumental = InstrumentalRegex.IsMatch(combined);
        bool isDemo = DemoRegex.IsMatch(combined);
        bool isRemaster = RemasterRegex.IsMatch(combined);

        string? specific = null;
        if (isLive) specific = "Live";
        else if (isRemix) specific = "Remix";
        else if (isAcoustic) specific = "Acoustic";
        else if (isInstrumental) specific = "Instrumental";
        else if (isDemo) specific = "Demo";
        else if (isRemaster) specific = "Remaster";

        return new VersionInfo(isLive, isRemix, isAcoustic, isInstrumental, isDemo, isRemaster, specific);
    }

    /// <summary>
    /// Computes token-based and Levenshtein similarity score between 0.0 and 1.0.
    /// </summary>
    public static double CalculateSimilarity(string? strA, string? strB)
    {
        string normA = Normalize(strA);
        string normB = Normalize(strB);

        if (string.IsNullOrEmpty(normA) && string.IsNullOrEmpty(normB)) return 1.0;
        if (string.IsNullOrEmpty(normA) || string.IsNullOrEmpty(normB)) return 0.0;
        if (normA == normB) return 1.0;

        // Containment check
        if (normA.Contains(normB) || normB.Contains(normA))
        {
            double ratio = (double)Math.Min(normA.Length, normB.Length) / Math.Max(normA.Length, normB.Length);
            return Math.Max(0.85, ratio);
        }

        // Levenshtein distance
        int dist = LevenshteinDistance(normA, normB);
        int maxLen = Math.Max(normA.Length, normB.Length);
        if (maxLen == 0) return 1.0;

        return Math.Max(0.0, 1.0 - ((double)dist / maxLen));
    }

    private static int LevenshteinDistance(string s, string t)
    {
        int n = s.Length;
        int m = t.Length;
        int[,] d = new int[n + 1, m + 1];

        if (n == 0) return m;
        if (m == 0) return n;

        for (int i = 0; i <= n; d[i, 0] = i++) ;
        for (int j = 0; j <= m; d[0, j] = j++) ;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    /// <summary>
    /// Parses filename patterns like "01 - Artist - Title.mp3", "Artist - Title.flac", "01 Title.mp3"
    /// when audio tags are missing.
    /// </summary>
    public static (string? ExtractedTitle, string? ExtractedArtist, string? ExtractedAlbum, int? ExtractedTrackNumber) ParseFromPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return (null, null, null, null);

        string fileName = Path.GetFileNameWithoutExtension(filePath);
        string? directoryName = Path.GetFileName(Path.GetDirectoryName(filePath));

        int? trackNum = null;
        string? artist = null;
        string? title = null;
        string? album = !string.IsNullOrWhiteSpace(directoryName) ? directoryName : null;

        // Pattern: "01 - Artist - Title" or "01. Artist - Title"
        var matchThreePart = Regex.Match(fileName, @"^(?:(?<num>\d{1,3})[\s\.\-_]+)?(?<artist>[^-]+)\s*-\s*(?<title>.+)$");
        if (matchThreePart.Success)
        {
            if (int.TryParse(matchThreePart.Groups["num"].Value, out int n))
                trackNum = n;
            artist = matchThreePart.Groups["artist"].Value.Trim();
            title = matchThreePart.Groups["title"].Value.Trim();
            return (title, artist, album, trackNum);
        }

        // Pattern: "01 - Title" or "01 Title"
        var matchTwoPart = Regex.Match(fileName, @"^(?<num>\d{1,3})[\s\.\-_]+(?<title>.+)$");
        if (matchTwoPart.Success)
        {
            if (int.TryParse(matchTwoPart.Groups["num"].Value, out int n))
                trackNum = n;
            title = matchTwoPart.Groups["title"].Value.Trim();
            return (title, null, album, trackNum);
        }

        return (fileName.Trim(), null, album, null);
    }
}
