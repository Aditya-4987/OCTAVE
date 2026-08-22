using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Interfaces.External;

public interface IExternalLyricsProvider
{
    string ProviderName { get; }
    bool IsEnabled { get; }
    int Priority { get; }

    Task<ExternalLyricsResult?> FetchLyricsAsync(
        string title,
        string artist,
        string? album = null,
        double? durationSeconds = null,
        ExternalIds? externalIds = null,
        CancellationToken ct = default);
}
