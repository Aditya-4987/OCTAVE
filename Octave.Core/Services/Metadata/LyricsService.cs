using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Interfaces;
using Octave.Core.Models;

namespace Octave.Core.Services.Metadata;

public class LyricsService : ILyricsService
{
    private static readonly Regex LrcTimestampRegex = new(
        @"\[(?<min>\d{1,3}):(?<sec>\d{2})(?:[\.:](?<ms>\d{2,3}))?\]",
        RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(
        @"\[\d{1,3}:\d{2}(?:[\.:]\d{2,3})?\]",
        RegexOptions.Compiled);

    public async Task<LyricsData> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (track == null || string.IsNullOrWhiteSpace(track.SourceUri))
        {
            return new LyricsData(track?.Id, LyricsState.Unavailable, null, null);
        }

        // Local file check only for local URIs
        if (track.SourceUri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return new LyricsData(track.Id, LyricsState.Unavailable, null, null);
        }

        string? lrcPath = FindLocalLrcFile(track.SourceUri);
        if (string.IsNullOrEmpty(lrcPath) || !File.Exists(lrcPath))
        {
            return new LyricsData(track.Id, LyricsState.Unavailable, null, null);
        }

        try
        {
            string rawContent = await File.ReadAllTextAsync(lrcPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return new LyricsData(track.Id, LyricsState.Unavailable, null, null);
            }

            return ParseLrcContent(track.Id, rawContent);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricsService] Error reading LRC file '{lrcPath}': {ex.Message}");
            return new LyricsData(track.Id, LyricsState.Unavailable, null, null);
        }
    }

    private static string? FindLocalLrcFile(string audioPath)
    {
        try
        {
            string candidate1 = Path.ChangeExtension(audioPath, ".lrc");
            if (File.Exists(candidate1)) return candidate1;

            string candidate2 = audioPath + ".lrc";
            if (File.Exists(candidate2)) return candidate2;
        }
        catch { }

        return null;
    }

    public static LyricsData ParseLrcContent(string trackId, string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return new LyricsData(trackId, LyricsState.Unavailable, null, null);
        }

        string[] lines = rawContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var syncedLines = new List<LyricLine>();
        var plainLines = new List<string>();

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var matches = LrcTimestampRegex.Matches(line);
            if (matches.Count > 0)
            {
                // Each line may have multiple timestamps e.g. [00:12.50][01:15.20]Lyric text
                string text = TagRegex.Replace(line, "").Trim();

                foreach (Match m in matches)
                {
                    int min = int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture);
                    int sec = int.Parse(m.Groups["sec"].Value, CultureInfo.InvariantCulture);
                    int ms = 0;
                    if (m.Groups["ms"].Success)
                    {
                        string msStr = m.Groups["ms"].Value;
                        ms = msStr.Length switch
                        {
                            1 => int.Parse(msStr, CultureInfo.InvariantCulture) * 100,
                            2 => int.Parse(msStr, CultureInfo.InvariantCulture) * 10,
                            _ => int.Parse(msStr[..3], CultureInfo.InvariantCulture)
                        };
                    }

                    var startTime = new TimeSpan(0, 0, min, sec, ms);
                    syncedLines.Add(new LyricLine(startTime, null, text));
                }
            }
            else if (!line.StartsWith("[ti:", StringComparison.OrdinalIgnoreCase) &&
                     !line.StartsWith("[ar:", StringComparison.OrdinalIgnoreCase) &&
                     !line.StartsWith("[al:", StringComparison.OrdinalIgnoreCase) &&
                     !line.StartsWith("[by:", StringComparison.OrdinalIgnoreCase) &&
                     !line.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase))
            {
                plainLines.Add(line);
            }
        }

        if (syncedLines.Count > 0)
        {
            var sorted = syncedLines.OrderBy(l => l.Start).ToList();
            var resultLines = new List<LyricLine>();

            for (int i = 0; i < sorted.Count; i++)
            {
                TimeSpan start = sorted[i].Start;
                TimeSpan? end = (i < sorted.Count - 1) ? sorted[i + 1].Start : null;
                resultLines.Add(new LyricLine(start, end, sorted[i].Text));
            }

            return new LyricsData(trackId, LyricsState.Synced, resultLines.AsReadOnly(), null);
        }
        else if (plainLines.Count > 0)
        {
            string fullText = string.Join(Environment.NewLine, plainLines);
            return new LyricsData(trackId, LyricsState.Unsynced, null, fullText);
        }

        return new LyricsData(trackId, LyricsState.Unavailable, null, null);
    }
}
