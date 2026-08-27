using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    // In-memory L1 cache for sub-millisecond instant lyrics delivery on repeated/pre-warmed tracks
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LyricsData> _memoryCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILrclibClient? _lrclibClient;
    private readonly ILyricsRepository? _lyricsRepository;
    private readonly ILyricsMatcher _lyricsMatcher;

    public LyricsService(ILrclibClient? lrclibClient = null, ILyricsRepository? lyricsRepository = null, ILyricsMatcher? lyricsMatcher = null)
    {
        _lrclibClient = lrclibClient;
        _lyricsRepository = lyricsRepository;
        _lyricsMatcher = lyricsMatcher ?? new LyricsMatcher();
    }

    public void InvalidateMemoryCache(string? trackId = null)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            _memoryCache.Clear();
        }
        else
        {
            _memoryCache.TryRemove(trackId, out _);
        }
    }

    /// <summary>
    /// Phase 1: Fast resolution from L1 memory cache, SQLite database cache, and local files (embedded tags, .lrc sidecars).
    /// Database-First design ensures zero redundant disk reads on already cached tracks (< 1ms).
    /// </summary>
    public async Task<LyricsData> GetLocalAndCachedLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (track == null || string.IsNullOrWhiteSpace(track.Id))
        {
            return new LyricsData(track?.Id, LyricsState.Unavailable, null, null);
        }

        // 0. L1 In-Memory Cache check (instant < 0.1ms)
        if (_memoryCache.TryGetValue(track.Id, out var memLyrics) && memLyrics != null)
        {
            return memLyrics;
        }

        var swFast = Stopwatch.StartNew();

        CachedLyricsEntity? cached = null;
        bool hasCachedSynced = false;
        string? cachedSyncedRaw = null;
        IReadOnlyList<LyricLine>? cachedSyncedLines = null;
        string? cachedSyncedSource = null;

        bool hasCachedPlain = false;
        string? cachedPlain = null;
        string? cachedPlainSource = null;

        // 1. Database-First: Check SQLite repository first (< 0.5ms)
        if (_lyricsRepository != null)
        {
            try
            {
                cached = await _lyricsRepository.GetCachedLyricsAsync(track.Id, cancellationToken);
                if (cached != null)
                {
                    // If negative cache is active and fresh, return Unavailable immediately without touching disk or network
                    if (cached.IsNotFound)
                    {
                        if (DateTimeOffset.UtcNow - cached.LastCheckedAt < NegativeCacheExpiration)
                        {
                            var notFoundData = new LyricsData(track.Id, LyricsState.Unavailable, null, null, false, false, null, false);
                            _memoryCache[track.Id] = notFoundData;
                            return notFoundData;
                        }
                    }
                    else
                    {
                        if (cached.HasSyncedLyrics && !string.IsNullOrWhiteSpace(cached.SyncedLyrics))
                        {
                            var parsed = ParseLrcContent(track.Id, cached.SyncedLyrics);
                            if (parsed.SyncedLines is { Count: > 0 })
                            {
                                hasCachedSynced = true;
                                cachedSyncedRaw = cached.SyncedLyrics;
                                cachedSyncedLines = parsed.SyncedLines;
                                cachedSyncedSource = cached.SyncedSource ?? cached.Source ?? "LRCLIB";
                            }
                        }

                        if (cached.HasPlainLyrics && !string.IsNullOrWhiteSpace(cached.PlainLyrics))
                        {
                            hasCachedPlain = true;
                            cachedPlain = cached.PlainLyrics;
                            cachedPlainSource = cached.StaticSource ?? cached.Source ?? "LRCLIB";
                        }

                        // LYRIC-01: if synced is present, derive plain text if missing
                        if (hasCachedSynced && !hasCachedPlain && cachedSyncedLines is { Count: > 0 })
                        {
                            hasCachedPlain = true;
                            cachedPlain = string.Join(Environment.NewLine, cachedSyncedLines.Select(l => l.Text));
                            cachedPlainSource = cachedSyncedSource;
                        }

                        // If SQLite cache contains complete lyrics, return IMMEDIATELY without touching disk file!
                        if (hasCachedSynced && hasCachedPlain)
                        {
                            string? finalSource = DetermineSourceComment(cachedSyncedSource, cachedPlainSource);
                            var completeCachedData = new LyricsData(
                                track.Id,
                                LyricsState.Synced,
                                cachedSyncedLines,
                                cachedPlain,
                                HasSyncedLyrics: true,
                                HasPlainLyrics: true,
                                RawSyncedLyrics: cachedSyncedRaw,
                                IsNetworkError: false,
                                SyncedSource: cachedSyncedSource,
                                StaticSource: cachedPlainSource,
                                Source: finalSource);

                            _memoryCache[track.Id] = completeCachedData;
                            return completeCachedData;
                        }
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

        // 2. Fallback to Local disk files (embedded audio tags & .lrc sidecars) ONLY when SQLite cache was missing/incomplete
        bool hasLocalSynced = false;
        string? localSyncedRaw = null;
        IReadOnlyList<LyricLine>? localSyncedLines = null;

        bool hasLocalPlain = false;
        string? localPlain = null;

        if (!string.IsNullOrWhiteSpace(track.SourceUri) && !track.SourceUri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // 2a. Check embedded tag lyrics
            if (File.Exists(track.SourceUri))
            {
                string? tagLyrics = null;
                try
                {
                    using var file = TagLib.File.Create(track.SourceUri);
                    tagLyrics = file.Tag?.Lyrics;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LyricsService] Reading embedded tags failed for '{track.SourceUri}': {ex.Message}");
                }

                if (!string.IsNullOrWhiteSpace(tagLyrics))
                {
                    var parsed = ParseLrcContent(track.Id, tagLyrics);
                    if (parsed.HasSyncedLyrics)
                    {
                        hasLocalSynced = true;
                        localSyncedRaw = tagLyrics;
                        localSyncedLines = parsed.SyncedLines;
                    }
                    if (parsed.HasPlainLyrics)
                    {
                        hasLocalPlain = true;
                        localPlain = parsed.PlainText;
                    }
                }
            }

            // 2b. Check sidecar .lrc file next if still missing
            if ((!hasCachedSynced && !hasLocalSynced) || (!hasCachedPlain && !hasLocalPlain))
            {
                try
                {
                    string? lrcPath = FindLocalLrcFile(track.SourceUri);
                    if (!string.IsNullOrEmpty(lrcPath) && File.Exists(lrcPath))
                    {
                        string lrcContent = await File.ReadAllTextAsync(lrcPath, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(lrcContent))
                        {
                            var parsed = ParseLrcContent(track.Id, lrcContent);
                            if (!hasLocalSynced && parsed.HasSyncedLyrics)
                            {
                                hasLocalSynced = true;
                                localSyncedRaw = lrcContent;
                                localSyncedLines = parsed.SyncedLines;
                            }
                            if (!hasLocalPlain && parsed.HasPlainLyrics)
                            {
                                hasLocalPlain = true;
                                localPlain = parsed.PlainText;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LyricsService] Reading sidecar .lrc failed for '{track.SourceUri}': {ex.Message}");
                }
            }
        }

        bool hasSynced = hasLocalSynced || hasCachedSynced;
        string? resolvedSyncedRaw = hasLocalSynced ? localSyncedRaw : cachedSyncedRaw;
        IReadOnlyList<LyricLine>? resolvedSyncedLines = hasLocalSynced ? localSyncedLines : cachedSyncedLines;
        string? syncedSource = hasLocalSynced ? "Local file" : cachedSyncedSource;

        bool hasPlain = hasLocalPlain || hasCachedPlain;
        string? resolvedPlain = hasLocalPlain ? localPlain : cachedPlain;
        string? plainSource = hasLocalPlain ? "Local file" : cachedPlainSource;

        // LYRIC-01: synced-only content is self-complete; derive plain text if needed
        if (hasSynced && !hasPlain && resolvedSyncedLines is { Count: > 0 })
        {
            hasPlain = true;
            resolvedPlain = string.Join(Environment.NewLine, resolvedSyncedLines.Select(l => l.Text));
            plainSource = plainSource ?? syncedSource;
        }

        // If local file contributed lyrics, persist to SQLite in background without blocking UI
        if (hasSynced && hasPlain && (hasLocalSynced || hasLocalPlain) && _lyricsRepository != null)
        {
            string? sourceToStore = DetermineSourceComment(syncedSource, plainSource);
            string? rPlain = resolvedPlain;
            string? rSynced = resolvedSyncedRaw;
            string? sSource = syncedSource;
            string? pSource = plainSource;
            long? rId = cached?.LrclibRecordId;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _lyricsRepository.UpsertCachedLyricsAsync(
                        track.Id,
                        rPlain,
                        rSynced,
                        hasPlainLyrics: true,
                        hasSyncedLyrics: true,
                        isNotFound: false,
                        source: sourceToStore,
                        lrclibRecordId: rId,
                        syncedSource: sSource,
                        staticSource: pSource,
                        cancellationToken: CancellationToken.None);
                }
                catch { }
            });
        }

        var resolvedState = hasSynced ? LyricsState.Synced : (hasPlain ? LyricsState.Unsynced : (cached != null && cached.IsNotFound ? LyricsState.Unavailable : LyricsState.Resolving));
        string? sourceComment = (hasSynced || hasPlain) ? DetermineSourceComment(syncedSource, plainSource) : null;

        var resultData = new LyricsData(
            track.Id,
            resolvedState,
            resolvedSyncedLines,
            resolvedPlain,
            HasSyncedLyrics: hasSynced,
            HasPlainLyrics: hasPlain,
            RawSyncedLyrics: resolvedSyncedRaw,
            IsNetworkError: false,
            SyncedSource: syncedSource,
            StaticSource: plainSource,
            Source: sourceComment);

        if (hasSynced || hasPlain)
        {
            _memoryCache[track.Id] = resultData;
        }

        return resultData;
    }

    /// <summary>
    /// Phase 2: Background enrichment from LRCLIB using Two-Stage Lookup (/api/get -> /api/search + scoring)
    /// for any format that is missing in <paramref name="currentLyrics"/>.
    /// </summary>
    public async Task<LyricsData> EnrichLyricsAsync(Track track, LyricsData currentLyrics, CancellationToken cancellationToken = default)
    {
        if (track == null || string.IsNullOrWhiteSpace(track.Id))
        {
            return currentLyrics ?? new LyricsData(track?.Id, LyricsState.Unavailable, null, null);
        }

        // If both formats are already resolved, do not query LRCLIB
        if (currentLyrics.HasSyncedLyrics && currentLyrics.HasPlainLyrics)
        {
            return currentLyrics;
        }

        bool canQueryLrclib = _lrclibClient != null &&
                              !string.IsNullOrWhiteSpace(track.Title) &&
                              !string.IsNullOrWhiteSpace(track.ArtistName);

        if (!canQueryLrclib)
        {
            return currentLyrics;
        }

        CachedLyricsEntity? cached = null;
        if (_lyricsRepository != null)
        {
            try
            {
                cached = await _lyricsRepository.GetCachedLyricsAsync(track.Id, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch { }
        }

        // Check if negative cache blocks remote query (only if we have NO local/cached lyrics and cache has a fresh NOT_FOUND)
        if (!currentLyrics.HasSyncedLyrics && !currentLyrics.HasPlainLyrics && cached != null && cached.IsNotFound)
        {
            if (DateTimeOffset.UtcNow - cached.LastCheckedAt < NegativeCacheExpiration)
            {
                return currentLyrics with { State = LyricsState.Unavailable };
            }
        }

        bool hasSynced = currentLyrics.HasSyncedLyrics;
        string? resolvedSyncedRaw = currentLyrics.RawSyncedLyrics;
        IReadOnlyList<LyricLine>? resolvedSyncedLines = currentLyrics.SyncedLines;
        string? syncedSource = hasSynced ? (currentLyrics.SyncedSource ?? currentLyrics.Source ?? "Local file") : null;

        bool hasPlain = currentLyrics.HasPlainLyrics;
        string? resolvedPlain = currentLyrics.PlainText;
        string? plainSource = hasPlain ? (currentLyrics.StaticSource ?? currentLyrics.Source ?? "Local file") : null;

        var swNetwork = Stopwatch.StartNew();

        try
        {
            LrclibResponse? response = null;

            // Step 0: If we already have a cached LrclibRecordId, fetch directly by ID
            if (cached?.LrclibRecordId.HasValue == true && cached.LrclibRecordId.Value > 0)
            {
                try
                {
                    response = await _lrclibClient!.GetLyricsByIdAsync(cached.LrclibRecordId.Value, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LyricsService] GetLyricsByIdAsync failed ({cached.LrclibRecordId.Value}): {ex.Message}");
                }
            }

            // Step 1: Direct /api/get lookup with best available metadata
            if (response == null || (string.IsNullOrWhiteSpace(response.PlainLyrics) && string.IsNullOrWhiteSpace(response.SyncedLyrics)))
            {
                response = await _lrclibClient!.GetLyricsAsync(
                    track.Title,
                    track.ArtistName,
                    track.AlbumTitle,
                    track.DurationSeconds,
                    cancellationToken);

                // If exact title failed and title contains soundtrack/attribution tags, retry /api/get with normalized title
                if (response == null || (string.IsNullOrWhiteSpace(response.PlainLyrics) && string.IsNullOrWhiteSpace(response.SyncedLyrics)))
                {
                    string normalizedTitle = _lyricsMatcher.NormalizeTitle(track.Title);
                    var artistSet = _lyricsMatcher.ParseArtistSet(track.ArtistName);
                    string? primaryArtist = artistSet.FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(normalizedTitle) && !string.IsNullOrWhiteSpace(primaryArtist) &&
                        (!string.Equals(normalizedTitle, track.Title.Trim(), StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(primaryArtist, track.ArtistName.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        var retryGet = await _lrclibClient!.GetLyricsAsync(
                            normalizedTitle,
                            primaryArtist,
                            null,
                            track.DurationSeconds,
                            cancellationToken);

                        if (retryGet != null && (!string.IsNullOrWhiteSpace(retryGet.PlainLyrics) || !string.IsNullOrWhiteSpace(retryGet.SyncedLyrics)))
                        {
                            response = retryGet;
                        }
                    }
                }
            }

            // Step 2: If /api/get produced no usable result, fall back to /api/search + Candidate Scoring
            if (response == null || (string.IsNullOrWhiteSpace(response.PlainLyrics) && string.IsNullOrWhiteSpace(response.SyncedLyrics)))
            {
                string normTitle = _lyricsMatcher.NormalizeTitle(track.Title);
                var artistSet = _lyricsMatcher.ParseArtistSet(track.ArtistName);
                string primaryArtist = artistSet.FirstOrDefault() ?? track.ArtistName;

                string query = $"{normTitle} {primaryArtist}".Trim();
                var searchCandidates = await _lrclibClient!.SearchLyricsAsync(
                    query: query,
                    cancellationToken: cancellationToken);

                if ((searchCandidates == null || searchCandidates.Count == 0) && !string.IsNullOrWhiteSpace(normTitle))
                {
                    // Fallback to structured search parameters
                    searchCandidates = await _lrclibClient!.SearchLyricsAsync(
                        trackName: normTitle,
                        artistName: primaryArtist,
                        cancellationToken: cancellationToken);
                }

                if (searchCandidates is { Count: > 0 })
                {
                    var (bestCandidate, score) = _lyricsMatcher.SelectBestCandidate(
                        track.Title,
                        track.ArtistName,
                        track.AlbumTitle,
                        track.DurationSeconds,
                        searchCandidates,
                        minScore: LyricsMatcher.DefaultConfidenceThreshold);

                    if (bestCandidate != null)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[LYRICS] Match found via /api/search: Track='{track.Title}' -> Candidate='{bestCandidate.TrackName}' (Id={bestCandidate.Id}, Score={score:F3})");

                        // If candidate has lyrics, use it; otherwise fetch full record by ID
                        if (!string.IsNullOrWhiteSpace(bestCandidate.PlainLyrics) || !string.IsNullOrWhiteSpace(bestCandidate.SyncedLyrics))
                        {
                            response = bestCandidate;
                        }
                        else
                        {
                            response = await _lrclibClient!.GetLyricsByIdAsync(bestCandidate.Id, cancellationToken);
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[LYRICS] No /api/search candidate met confidence threshold ({LyricsMatcher.DefaultConfidenceThreshold}) for '{track.Title}'");
                    }
                }
            }

            long netMs = swNetwork.ElapsedMilliseconds;

            if (response == null || response.Instrumental ||
                (string.IsNullOrWhiteSpace(response.PlainLyrics) && string.IsNullOrWhiteSpace(response.SyncedLyrics)))
            {
                // LRCLIB has no lyrics for this track
                if (!hasSynced && !hasPlain)
                {
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
                                source: null,
                                lrclibRecordId: null,
                                syncedSource: null,
                                staticSource: null,
                                cancellationToken: cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[LyricsService] Caching negative result failed: {ex.Message}");
                        }
                    }

                    return new LyricsData(track.Id, LyricsState.Unavailable, null, null, false, false, null, false);
                }

                var existingState = hasSynced ? LyricsState.Synced : (hasPlain ? LyricsState.Unsynced : LyricsState.Unavailable);
                return new LyricsData(
                    track.Id,
                    existingState,
                    resolvedSyncedLines,
                    resolvedPlain,
                    hasSynced,
                    hasPlain,
                    resolvedSyncedRaw,
                    false,
                    syncedSource,
                    plainSource,
                    DetermineSourceComment(syncedSource, plainSource));
            }

            // Process retrieved LRCLIB formats
            if (!hasSynced && !string.IsNullOrWhiteSpace(response.SyncedLyrics))
            {
                var parsed = ParseLrcContent(track.Id, response.SyncedLyrics);
                if (parsed.SyncedLines is { Count: > 0 })
                {
                    hasSynced = true;
                    resolvedSyncedRaw = response.SyncedLyrics;
                    resolvedSyncedLines = parsed.SyncedLines;
                    syncedSource = "LRCLIB";
                }
            }

            if (!hasPlain && !string.IsNullOrWhiteSpace(response.PlainLyrics))
            {
                hasPlain = true;
                resolvedPlain = response.PlainLyrics;
                plainSource = "LRCLIB";
            }

            string? finalSource = DetermineSourceComment(syncedSource, plainSource);

            // Cache combined result in SQLite along with matched LRCLIB record ID
            if (_lyricsRepository != null)
            {
                try
                {
                    await _lyricsRepository.UpsertCachedLyricsAsync(
                        track.Id,
                        resolvedPlain,
                        resolvedSyncedRaw,
                        hasPlain,
                        hasSynced,
                        isNotFound: false,
                        source: finalSource,
                        lrclibRecordId: response.Id > 0 ? response.Id : cached?.LrclibRecordId,
                        syncedSource: syncedSource,
                        staticSource: plainSource,
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LyricsService] Caching combined lyrics failed: {ex.Message}");
                }
            }

            var resolvedState = hasSynced ? LyricsState.Synced : (hasPlain ? LyricsState.Unsynced : LyricsState.Unavailable);

            var enrichedResult = new LyricsData(
                track.Id,
                resolvedState,
                resolvedSyncedLines,
                resolvedPlain,
                hasSynced,
                hasPlain,
                resolvedSyncedRaw,
                false,
                syncedSource,
                plainSource,
                finalSource);

            if (hasSynced || hasPlain)
            {
                _memoryCache[track.Id] = enrichedResult;
            }

            return enrichedResult;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HTTP timeout
            long netMs = swNetwork.ElapsedMilliseconds;
            System.Diagnostics.Debug.WriteLine($"[LyricsService] LRCLIB request timed out for '{track.Title}' ({netMs}ms): {ex.Message}");

            if (hasSynced || hasPlain)
            {
                var fallbackState = hasSynced ? LyricsState.Synced : (hasPlain ? LyricsState.Unsynced : LyricsState.NetworkUnavailable);
                return new LyricsData(
                    track.Id,
                    fallbackState,
                    resolvedSyncedLines,
                    resolvedPlain,
                    hasSynced,
                    hasPlain,
                    resolvedSyncedRaw,
                    IsNetworkError: true,
                    SyncedSource: syncedSource,
                    StaticSource: plainSource,
                    Source: DetermineSourceComment(syncedSource, plainSource));
            }

            return new LyricsData(track.Id, LyricsState.NetworkUnavailable, null, null, false, false, null, IsNetworkError: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            long netMs = swNetwork.ElapsedMilliseconds;
            System.Diagnostics.Debug.WriteLine($"[LyricsService] LRCLIB request failed for '{track.Title}' ({netMs}ms): {ex.Message}");

            if (hasSynced || hasPlain)
            {
                var fallbackState = hasSynced ? LyricsState.Synced : (hasPlain ? LyricsState.Unsynced : LyricsState.NetworkUnavailable);
                return new LyricsData(
                    track.Id,
                    fallbackState,
                    resolvedSyncedLines,
                    resolvedPlain,
                    hasSynced,
                    hasPlain,
                    resolvedSyncedRaw,
                    IsNetworkError: true,
                    SyncedSource: syncedSource,
                    StaticSource: plainSource,
                    Source: DetermineSourceComment(syncedSource, plainSource));
            }

            return new LyricsData(track.Id, LyricsState.NetworkUnavailable, null, null, false, false, null, IsNetworkError: true);
        }
    }

    /// <summary>
    /// Forces a refresh from remote LRCLIB provider: invalidates existing SQLite cache,
    /// queries LRCLIB remotely (Two-Stage: /api/get -> /api/search + scoring), replaces SQLite cache,
    /// and never modifies local audio file tags.
    /// </summary>
    public async Task<LyricsData> ReloadLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (track == null || string.IsNullOrWhiteSpace(track.Id))
        {
            return new LyricsData(track?.Id, LyricsState.Unavailable, null, null);
        }

        // 1. Invalidate L1 memory cache and SQLite cache record
        InvalidateMemoryCache(track.Id);
        if (_lyricsRepository != null)
        {
            try
            {
                await _lyricsRepository.DeleteCachedLyricsAsync(track.Id, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LyricsService] Invalidate cache for '{track.Id}' failed: {ex.Message}");
            }
        }

        // 1b. LYRIC-02: snapshot locally-available lyrics (embedded tags / sidecar .lrc)
        // BEFORE the remote query. A failed remote refresh (offline, timeout, no match)
        // falls back to these instead of blanking the panel and discarding lyrics that
        // still exist locally. The cache was just deleted, so this reads the file only.
        LyricsData localLyrics;
        try
        {
            localLyrics = await GetLocalAndCachedLyricsAsync(track, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            localLyrics = new LyricsData(track.Id, LyricsState.Unavailable, null, null);
        }

        // 2. Query remote LRCLIB provider directly
        if (_lrclibClient == null || string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.ArtistName))
        {
            return new LyricsData(track.Id, LyricsState.Unavailable, null, null);
        }

        var swNetwork = Stopwatch.StartNew();

        try
        {
            LrclibResponse? response = null;

            // Step 1: Direct /api/get lookup with best available metadata
            response = await _lrclibClient.GetLyricsAsync(
                track.Title,
                track.ArtistName,
                track.AlbumTitle,
                track.DurationSeconds,
                cancellationToken);

            // Retry with normalized title / soundtrack cleanup if needed
            if (response == null || (string.IsNullOrWhiteSpace(response.PlainLyrics) && string.IsNullOrWhiteSpace(response.SyncedLyrics)))
            {
                string normalizedTitle = _lyricsMatcher.NormalizeTitle(track.Title);
                var artistSet = _lyricsMatcher.ParseArtistSet(track.ArtistName);
                string? primaryArtist = artistSet.FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(normalizedTitle) && !string.IsNullOrWhiteSpace(primaryArtist) &&
                    (!string.Equals(normalizedTitle, track.Title.Trim(), StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(primaryArtist, track.ArtistName.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    var retryGet = await _lrclibClient.GetLyricsAsync(
                        normalizedTitle,
                        primaryArtist,
                        null,
                        track.DurationSeconds,
                        cancellationToken);

                    if (retryGet != null && (!string.IsNullOrWhiteSpace(retryGet.PlainLyrics) || !string.IsNullOrWhiteSpace(retryGet.SyncedLyrics)))
                    {
                        response = retryGet;
                    }
                }
            }

            // Step 2: Fall back to /api/search + Candidate Scoring
            if (response == null || (string.IsNullOrWhiteSpace(response.PlainLyrics) && string.IsNullOrWhiteSpace(response.SyncedLyrics)))
            {
                string normTitle = _lyricsMatcher.NormalizeTitle(track.Title);
                var artistSet = _lyricsMatcher.ParseArtistSet(track.ArtistName);
                string primaryArtist = artistSet.FirstOrDefault() ?? track.ArtistName;

                string query = $"{normTitle} {primaryArtist}".Trim();
                var searchCandidates = await _lrclibClient.SearchLyricsAsync(
                    query: query,
                    cancellationToken: cancellationToken);

                if ((searchCandidates == null || searchCandidates.Count == 0) && !string.IsNullOrWhiteSpace(normTitle))
                {
                    searchCandidates = await _lrclibClient.SearchLyricsAsync(
                        trackName: normTitle,
                        artistName: primaryArtist,
                        cancellationToken: cancellationToken);
                }

                if (searchCandidates is { Count: > 0 })
                {
                    var (bestCandidate, score) = _lyricsMatcher.SelectBestCandidate(
                        track.Title,
                        track.ArtistName,
                        track.AlbumTitle,
                        track.DurationSeconds,
                        searchCandidates,
                        minScore: LyricsMatcher.DefaultConfidenceThreshold);

                    if (bestCandidate != null)
                    {
                        if (!string.IsNullOrWhiteSpace(bestCandidate.PlainLyrics) || !string.IsNullOrWhiteSpace(bestCandidate.SyncedLyrics))
                        {
                            response = bestCandidate;
                        }
                        else
                        {
                            response = await _lrclibClient.GetLyricsByIdAsync(bestCandidate.Id, cancellationToken);
                        }
                    }
                }
            }

            long netMs = swNetwork.ElapsedMilliseconds;

            if (response == null || response.Instrumental ||
                (string.IsNullOrWhiteSpace(response.PlainLyrics) && string.IsNullOrWhiteSpace(response.SyncedLyrics)))
            {
                // LYRIC-02: a failed remote refresh must not blank the panel when lyrics
                // are still available locally — fall back to them first.
                if (localLyrics.HasSyncedLyrics || localLyrics.HasPlainLyrics)
                {
                    return localLyrics;
                }

                // Store negative cache
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
                            source: null,
                            lrclibRecordId: null,
                            syncedSource: null,
                            staticSource: null,
                            cancellationToken: cancellationToken);
                    }
                    catch { }
                }

                return new LyricsData(track.Id, LyricsState.Unavailable, null, null, false, false, null, false);
            }

            bool hasSynced = false;
            string? resolvedSyncedRaw = null;
            IReadOnlyList<LyricLine>? resolvedSyncedLines = null;
            string? syncedSource = null;

            if (!string.IsNullOrWhiteSpace(response.SyncedLyrics))
            {
                var parsed = ParseLrcContent(track.Id, response.SyncedLyrics);
                if (parsed.SyncedLines is { Count: > 0 })
                {
                    hasSynced = true;
                    resolvedSyncedRaw = response.SyncedLyrics;
                    resolvedSyncedLines = parsed.SyncedLines;
                    syncedSource = "LRCLIB";
                }
            }

            bool hasPlain = false;
            string? resolvedPlain = null;
            string? plainSource = null;

            if (!string.IsNullOrWhiteSpace(response.PlainLyrics))
            {
                hasPlain = true;
                resolvedPlain = response.PlainLyrics;
                plainSource = "LRCLIB";
            }

            string? finalSource = DetermineSourceComment(syncedSource, plainSource);

            // Replace complete cache record
            if (_lyricsRepository != null)
            {
                try
                {
                    await _lyricsRepository.UpsertCachedLyricsAsync(
                        track.Id,
                        resolvedPlain,
                        resolvedSyncedRaw,
                        hasPlain,
                        hasSynced,
                        isNotFound: false,
                        source: finalSource,
                        lrclibRecordId: response.Id > 0 ? response.Id : null,
                        syncedSource: syncedSource,
                        staticSource: plainSource,
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LyricsService] Reload caching failed: {ex.Message}");
                }
            }

            var resolvedState = hasSynced ? LyricsState.Synced : (hasPlain ? LyricsState.Unsynced : LyricsState.Unavailable);

            var reloadResult = new LyricsData(
                track.Id,
                resolvedState,
                resolvedSyncedLines,
                resolvedPlain,
                hasSynced,
                hasPlain,
                resolvedSyncedRaw,
                false,
                syncedSource,
                plainSource,
                finalSource);

            if (hasSynced || hasPlain)
            {
                _memoryCache[track.Id] = reloadResult;
            }

            return reloadResult;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HTTP timeout — LYRIC-02: fall back to local lyrics rather than blanking.
            if (localLyrics.HasSyncedLyrics || localLyrics.HasPlainLyrics)
            {
                return localLyrics;
            }
            return new LyricsData(track.Id, LyricsState.NetworkUnavailable, null, null, false, false, null, IsNetworkError: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricsService] Reload failed: {ex.Message}");
            if (localLyrics.HasSyncedLyrics || localLyrics.HasPlainLyrics)
            {
                return localLyrics;
            }
            return new LyricsData(track.Id, LyricsState.NetworkUnavailable, null, null, false, false, null, IsNetworkError: true);
        }
    }

    /// <summary>
    /// Full resolution pipeline: Phase 1 (Local + Cache) followed by Phase 2 (Background LRCLIB) if missing.
    /// </summary>
    public async Task<LyricsData> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        var localAndCached = await GetLocalAndCachedLyricsAsync(track, cancellationToken);
        if (localAndCached.HasSyncedLyrics && localAndCached.HasPlainLyrics)
        {
            return localAndCached;
        }
        return await EnrichLyricsAsync(track, localAndCached, cancellationToken);
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

    private static string? DetermineSourceComment(string? syncedSource, string? plainSource)
    {
        bool hasLocal = syncedSource == "Local file" || plainSource == "Local file";
        bool hasLrclib = syncedSource == "LRCLIB" || plainSource == "LRCLIB";

        if (hasLocal && hasLrclib)
        {
            return "Local / LRCLIB";
        }
        if (hasLocal)
        {
            return "Local file";
        }
        if (hasLrclib)
        {
            return "LRCLIB";
        }
        return syncedSource ?? plainSource ?? null;
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

            // LYRIC-01: a synced-only LRC has no explicit plain lines, but the
            // timestamped lines ARE the lyrics. Deriving the plain view from them keeps
            // HasPlainLyrics=true, so the "complete" gates (Phase 1 cache write and the
            // EnrichLyricsAsync short-circuit) are satisfied. That stops the every-play
            // LRCLIB fetch for a missing-plain gap (which nothing negative-caches) and
            // keeps the Static view populated offline. LRCLIB derives its own plain text
            // from the same synced lines, so there is no fidelity loss.
            bool hasPlain = true;
            string? plainText;
            if (plainLines.Count > 0)
            {
                plainText = string.Join(Environment.NewLine, plainLines);
            }
            else
            {
                plainText = string.Join(Environment.NewLine, sorted.Select(l => l.Text));
            }

            return new LyricsData(
                trackId,
                LyricsState.Synced,
                resultLines.AsReadOnly(),
                plainText,
                HasSyncedLyrics: true,
                HasPlainLyrics: hasPlain,
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
