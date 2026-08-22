using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Services.External;

public interface IOnlineLyricsOrchestrator
{
    Task<LyricsData> FetchLyricsAsync(
        string title,
        string artist,
        string? album = null,
        double? durationSeconds = null,
        ExternalIds? externalIds = null,
        CancellationToken ct = default);
}
