using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Interfaces;

public interface ILrclibClient
{
    Task<LrclibResponse?> GetLyricsAsync(
        string trackTitle,
        string artistName,
        string? albumName = null,
        double? durationSeconds = null,
        CancellationToken cancellationToken = default);

    Task<LrclibResponse?> GetLyricsByIdAsync(
        long id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LrclibResponse>> SearchLyricsAsync(
        string? query = null,
        string? trackName = null,
        string? artistName = null,
        string? albumName = null,
        CancellationToken cancellationToken = default);
}
