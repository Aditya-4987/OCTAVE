using System;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.External;

namespace Octave.Core.Services.Metadata;

public class CompositeLyricsService : ILyricsService
{
    private readonly LyricsService _localLyricsService;
    private readonly IOnlineLyricsOrchestrator _onlineOrchestrator;

    public CompositeLyricsService(
        LyricsService localLyricsService,
        IOnlineLyricsOrchestrator onlineOrchestrator)
    {
        _localLyricsService = localLyricsService ?? throw new ArgumentNullException(nameof(localLyricsService));
        _onlineOrchestrator = onlineOrchestrator ?? throw new ArgumentNullException(nameof(onlineOrchestrator));
    }

    public async Task<LyricsData> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (track == null)
        {
            return new LyricsData(null, LyricsState.Unavailable, null, null);
        }

        // 1. Check local .lrc files first (offline-first priority)
        var localResult = await _localLyricsService.GetLyricsAsync(track, cancellationToken).ConfigureAwait(false);
        if (localResult.State == LyricsState.Synced || localResult.State == LyricsState.Unsynced)
        {
            return localResult;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new LyricsData(track.Id, LyricsState.Unavailable, null, null);
        }

        // 2. Query online providers through orchestrator if local is unavailable
        var onlineResult = await _onlineOrchestrator.FetchLyricsAsync(
            track.Title,
            track.ArtistName,
            track.AlbumTitle,
            track.DurationSeconds,
            null,
            cancellationToken).ConfigureAwait(false);

        if (onlineResult.State == LyricsState.Synced || onlineResult.State == LyricsState.Unsynced)
        {
            return new LyricsData(track.Id, onlineResult.State, onlineResult.SyncedLines, onlineResult.PlainText);
        }

        return new LyricsData(track.Id, LyricsState.Unavailable, null, null);
    }
}
