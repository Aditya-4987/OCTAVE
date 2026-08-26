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
    // NF-18: fractional part accepts ONE to three digits — real .lrc files do
    // appear as "[00:04.5]"; the millisecond normalizer below already scales
    // 1-digit (×100), 2-digit (×10) and 3-digit (as-is) values.
    private static readonly Regex LrcTimestampRegex = new(
        @"\[(?<min>\d{1,3}):(?<sec>\d{2})(?:[\.:](?<ms>\d{1,3}))?\]",
        RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(
        @"\[\d{1,3}:\d{2}(?:[\.:]\d{1,3})?\]",
        RegexOptions.Compiled);

    private static readonly Regex OffsetRegex = new(
        @"^\[offset:\s*(?<offset>[+-]?\d+)\s*\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static readonly TimeSpan NegativeCacheExpiration = TimeSpan.FromDays(7);

    private readonly ILrclibClient? _lrclibClient;
    private readonly ILyricsRepository? _lyricsRepository;

    public LyricsService(ILrclibClient? lrclibClient = null, ILyricsRepository? lyricsRepository = null)
    {
        _lrclibClient = lrclibClient;
        _lyricsRepository = lyricsRepository;
    }

    public async Task<LyricsData> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (track == null || string.IsNullOrWhiteSpace(track.Id))
        {
            return new LyricsData(track?.Id, LyricsState.Unavailable, null, null);
        }

        // 1. Check local file (embedded tags and sidecar .lrc)
        if (!string.IsNullOrWhiteSpace(track.SourceUri) && !track.SourceUri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // 1a. Check embedded tag lyrics first
            if (File.Exists(track.SourceUri))
            {
                try
                {
                    using var tagFile = TagLib.File.Create(track.SourceUri);
                    string? embedded = tagFile.Tag.Lyrics;
                    if (!string.IsNullOrWhiteSpace(embedded))
                    {
                        var parsedEmbedded = ParseLrcContent(track.Id, embedded);
                        if (parsedEmbedded.State == LyricsState.Synced || parsedEmbedded.State == LyricsState.Unsynced)
                        {
                            if (_lyricsRepository != null)
                            {
                                try
                                {
                                    await _lyricsRepository.UpsertCachedLyricsAsync(
                                        track.Id,
                                        parsedEmbedded.PlainText,
                                        parsedEmbedded.RawSyncedLyrics,
                                        parsedEmbedded.HasPlainLyrics,
                                        parsedEmbedded.HasSyncedLyrics,
                                        isNotFound: false,
                                        cancellationToken);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[LyricsService] Caching local embedded lyrics failed: {ex.Message}");
                                }
                            }
                            return parsedEmbedded;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LyricsService] Embedded tag reading failed for '{track.SourceUri}': {ex.Message}");
                }
            }

            // 1b. Check local LRC sidecar files
            string? lrcPath = FindLocalLrcFile(track.SourceUri);
            if (!string.IsNullOrEmpty(lrcPath) && File.Exists(lrcPath))
            {
                try
                {
                    string rawContent = await File.ReadAllTextAsync(lrcPath, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(rawContent))
                    {
                        var parsedLrc = ParseLrcContent(track.Id, rawContent);
                        if (parsedLrc.State == LyricsState.Synced || parsedLrc.State == LyricsState.Unsynced)
                        {
                            if (_lyricsRepository != null)
                            {
                                try
                                {
                                    await _lyricsRepository.UpsertCachedLyricsAsync(
                                        track.Id,
                                        parsedLrc.PlainText,
                                        parsedLrc.RawSyncedLyrics,
                                        parsedLrc.HasPlainLyrics,
                                        parsedLrc.HasSyncedLyrics,
                                        isNotFound: false,
                                        cancellationToken);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[LyricsService] Caching local sidecar lyrics failed: {ex.Message}");
                                }
                            }
                            return parsedLrc;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LyricsService] Error reading LRC file '{lrcPath}': {ex.Message}");
                }
            }
        }

        // 2. Check local database lyrics cache via repository
        if (_lyricsRepository != null)
        {
            try
            {
                var cached = await _lyricsRepository.GetCachedLyricsAsync(track.Id, cancellationToken);
                if (cached != null)
                {
                    if (cached.IsNotFound)
                    {
                        // Negative cache: verify whether it's within the refresh window
                        if (DateTimeOffset.UtcNow - cached.LastCheckedAt < NegativeCacheExpiration)
                        {
                            return new LyricsData(track.Id, LyricsState.Unavailable, null, null, false, false, null, false);
                        }
                        // Expired negative cache entry -> fall through to retry LRCLIB below
                    }
                    else if (cached.HasSyncedLyrics || cached.HasPlainLyrics)
                    {
                        IReadOnlyList<LyricLine>? syncedLines = null;
                        if (cached.HasSyncedLyrics && !string.IsNullOrWhiteSpace(cached.SyncedLyrics))
                        {
                            var parsed = ParseLrcContent(track.Id, cached.SyncedLyrics);
                            syncedLines = parsed.SyncedLines;
                        }

                        string? plainText = cached.PlainLyrics;
                        bool hasSynced = cached.HasSyncedLyrics && syncedLines != null && syncedLines.Count > 0;
                        bool hasPlain = cached.HasPlainLyrics && !string.IsNullOrWhiteSpace(plainText);

                        var state = hasSynced ? LyricsState.Synced : (hasPlain ? LyricsState.Unsynced : LyricsState.Unavailable);
                        return new LyricsData(track.Id, state, syncedLines, plainText, hasSynced, hasPlain, cached.SyncedLyrics, false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LyricsService] Database cache lookup failed: {ex.Message}");
            }
        }

        // 3. Query remote LRCLIB API
        if (_lrclibClient != null && !string.IsNullOrWhiteSpace(track.Title) && !string.IsNullOrWhiteSpace(track.ArtistName))
        {
            try
            {
                var response = await _lrclibClient.GetLyricsAsync(
                    track.Title,
                    track.ArtistName,
                    track.AlbumTitle,
                    track.DurationSeconds,
                    cancellationToken);

                if (response == null || response.Instrumental ||
                    (string.IsNullOrWhiteSpace(response.PlainLyrics) && string.IsNullOrWhiteSpace(response.SyncedLyrics)))
                {
                    // Track not found or instrumental: cache as NOT FOUND
                    if (_lyricsRepository != null)
                    {
                        try
                        {
                            await _lyricsRepository.UpsertCachedLyricsAsync(
                                track.Id,
                                null,
                                null,
                                hasPlainLyrics: false,
                                hasSyncedLyrics: false,
                                isNotFound: true,
                                cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[LyricsService] Negative cache upsert failed: {ex.Message}");
                        }
                    }

                    return new LyricsData(track.Id, LyricsState.Unavailable, null, null, false, false, null, false);
                }

                // Successful response with lyrics
                bool hasSynced = !string.IsNullOrWhiteSpace(response.SyncedLyrics);
                bool hasPlain = !string.IsNullOrWhiteSpace(response.PlainLyrics);
                IReadOnlyList<LyricLine>? syncedLines = null;

                if (hasSynced)
                {
                    var parsed = ParseLrcContent(track.Id, response.SyncedLyrics!);
                    syncedLines = parsed.SyncedLines;
                    if (syncedLines == null || syncedLines.Count == 0)
                    {
                        hasSynced = false;
                    }
                }

                string? plainLyrics = response.PlainLyrics;

                if (_lyricsRepository != null)
                {
                    try
                    {
                        await _lyricsRepository.UpsertCachedLyricsAsync(
                            track.Id,
                            plainLyrics,
                            response.SyncedLyrics,
                            hasPlain,
                            hasSynced,
                            isNotFound: false,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LyricsService] Caching LRCLIB lyrics failed: {ex.Message}");
                    }
                }

                var resolvedState = hasSynced ? LyricsState.Synced : (hasPlain ? LyricsState.Unsynced : LyricsState.Unavailable);
                return new LyricsData(track.Id, resolvedState, syncedLines, plainLyrics, hasSynced, hasPlain, response.SyncedLyrics, false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LyricsService] LRCLIB request failed for '{track.Title}': {ex.Message}");
                // On temporary network failure / error, DO NOT mark as not found in DB
                return new LyricsData(track.Id, LyricsState.Unavailable, null, null, false, false, null, IsNetworkError: true);
            }
        }

        return new LyricsData(track.Id, LyricsState.Unavailable, null, null, false, false, null, false);
    }

    private static string? FindLocalLrcFile(string audioPath)
    {
        try
        {
            string candidate1 = Path.ChangeExtension(audioPath, ".lrc");
            if (File.Exists(candidate1)) return candidate1;

            string candidate2 = audioPath + ".lrc";
            if (File.Exists(candidate2)) return candidate2;

            string? dir = Path.GetDirectoryName(audioPath);
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(audioPath);
            string fileNameWithExt = Path.GetFileName(audioPath);

            if (!string.IsNullOrEmpty(dir))
            {
                // Check dedicated "Lyrics" subfolder
                string lyricsSubdir = Path.Combine(dir, "Lyrics");
                if (Directory.Exists(lyricsSubdir))
                {
                    string candidate3 = Path.Combine(lyricsSubdir, fileNameWithoutExt + ".lrc");
                    if (File.Exists(candidate3)) return candidate3;

                    string candidate4 = Path.Combine(lyricsSubdir, fileNameWithExt + ".lrc");
                    if (File.Exists(candidate4)) return candidate4;
                }

                // Check dedicated "lyrics" subfolder
                string lowerLyricsSubdir = Path.Combine(dir, "lyrics");
                if (Directory.Exists(lowerLyricsSubdir))
                {
                    string candidate5 = Path.Combine(lowerLyricsSubdir, fileNameWithoutExt + ".lrc");
                    if (File.Exists(candidate5)) return candidate5;

                    string candidate6 = Path.Combine(lowerLyricsSubdir, fileNameWithExt + ".lrc");
                    if (File.Exists(candidate6)) return candidate6;
                }
            }
        }
        catch { }

        return null;
    }

    // TEST-04: the active-line seek lookup used to live only inside
    // NowPlayingViewModel.UpdateLyricPosition (and was "tested" by a private copy in
    // LyricsServiceTests that could never catch a regression). Extracted here as the
    // single production implementation: returns the index of the last line whose
    // Start is at or before the position, or -1 when every line starts later.
    public static int FindActiveLineIndex(IReadOnlyList<LyricLine>? lines, TimeSpan position)
    {
        if (lines == null || lines.Count == 0) return -1;

        int low = 0;
        int high = lines.Count - 1;
        int found = -1;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (lines[mid].Start <= position)
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

    public static LyricsData ParseLrcContent(string trackId, string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return new LyricsData(trackId, LyricsState.Unavailable, null, null, false, false, null, false);
        }

        string[] lines = rawContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var syncedLines = new List<LyricLine>();
        var plainLines = new List<string>();

        // Check for global [offset:+/-ms] tag
        int offsetMs = 0;
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var offsetMatch = OffsetRegex.Match(line);
            if (offsetMatch.Success && int.TryParse(offsetMatch.Groups["offset"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedOffset))
            {
                offsetMs = parsedOffset;
                break;
            }
        }

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
                    if (offsetMs != 0)
                    {
                        // Positive offset shifts time earlier (sooner), negative offset shifts time later
                        var adjusted = startTime - TimeSpan.FromMilliseconds(offsetMs);
                        startTime = adjusted < TimeSpan.Zero ? TimeSpan.Zero : adjusted;
                    }

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

            // Generate plain text version from the synced lines or plain lines
            string plainText = plainLines.Count > 0
                ? string.Join(Environment.NewLine, plainLines)
                : string.Join(Environment.NewLine, resultLines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

            return new LyricsData(
                trackId,
                LyricsState.Synced,
                resultLines.AsReadOnly(),
                plainText,
                HasSyncedLyrics: true,
                HasPlainLyrics: !string.IsNullOrWhiteSpace(plainText),
                RawSyncedLyrics: rawContent,
                IsNetworkError: false);
        }
        else if (plainLines.Count > 0)
        {
            string fullText = string.Join(Environment.NewLine, plainLines);
            return new LyricsData(
                trackId,
                LyricsState.Unsynced,
                null,
                fullText,
                HasSyncedLyrics: false,
                HasPlainLyrics: true,
                RawSyncedLyrics: null,
                IsNetworkError: false);
        }

        return new LyricsData(trackId, LyricsState.Unavailable, null, null, false, false, null, false);
    }
}
