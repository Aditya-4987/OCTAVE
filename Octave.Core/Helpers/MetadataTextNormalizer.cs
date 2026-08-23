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
    /// Analyzes the TITLE for version identifiers (Live, Remix, Acoustic, Instrumental,
    /// Demo, Remaster). MATCH-05: version markers describe the recording, so album
    /// keywords are intentionally ignored — an album named "Live at Wembley" must not
    /// brand every studio track on it as a live version and demote its candidates
    /// through the version-mismatch gates.
    /// </summary>
    public static VersionInfo ExtractVersionInfo(string? title, string? album = null)
    {
        // The album argument stays on the signature for call-site compatibility,
        // but only the title may assert a version.
        string source = title ?? "";

        bool isLive = LiveRegex.IsMatch(source);
        bool isRemix = RemixRegex.IsMatch(source);
        bool isAcoustic = AcousticRegex.IsMatch(source);
        bool isInstrumental = InstrumentalRegex.IsMatch(source);
        bool isDemo = DemoRegex.IsMatch(source);
        bool isRemaster = RemasterRegex.IsMatch(source);

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

        // Containment check (MATCH-02): containment is only a strong signal when
        // the shorter string is a substantial fraction of the longer one, or a
        // WHOLE WORD inside it. A 2-character fragment like "go" inside
        // "goodbye" used to floor the score at 0.85 and clear the auto-apply
        // gates; weak containment now falls through to plain Levenshtein and
        // scores on its own (low) merits.
        if (normA.Contains(normB, StringComparison.Ordinal) || normB.Contains(normA, StringComparison.Ordinal))
        {
            double ratio = (double)Math.Min(normA.Length, normB.Length) / Math.Max(normA.Length, normB.Length);
            if (ratio >= 0.5 || IsWholeWordInside(normA, normB))
            {
                return Math.Max(0.85, ratio);
            }
        }

        // Levenshtein distance
        int dist = LevenshteinDistance(normA, normB);
        int maxLen = Math.Max(normA.Length, normB.Length);
        if (maxLen == 0) return 1.0;

        return Math.Max(0.0, 1.0 - ((double)dist / maxLen));
    }

    // MATCH-02: true when <code>shorter</code> appears in <code>longer</code>
    // delimited by string boundaries or spaces (normalized text is lowercase
    // with single-space separators, so ' ' is the word delimiter).
    private static bool IsWholeWordInside(string longer, string shorter)
    {
        if (shorter.Length == 0 || shorter.Length > longer.Length) return false;

        int idx = longer.IndexOf(shorter, StringComparison.Ordinal);
        while (idx >= 0)
        {
            bool leftOk = idx == 0 || longer[idx - 1] == ' ';
            bool rightOk = idx + shorter.Length == longer.Length || longer[idx + shorter.Length] == ' ';
            if (leftOk && rightOk) return true;
            idx = longer.IndexOf(shorter, idx + 1, StringComparison.Ordinal);
        }

        return false;
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

        // Pattern: "01 - Artist - Title" or "01. Artist - Title".
        // MATCH-04: the artist/title separator must be a dash standing ALONE
        // between spaces — any-hyphen splitting parsed "Spider-Man Theme" as
        // artist "Spider", title "Man Theme". A leading track number still
        // consumes its separator greedily so "01 - Artist - Title" works, and
        // unspaced-hyphen names fall through to the number/filename patterns.
        var matchThreePart = Regex.Match(fileName, @"^(?:(?<num>\d{1,3})[\s\.\-_]+)?(?<artist>.+?)\s+-\s+(?<title>.+)$");
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
