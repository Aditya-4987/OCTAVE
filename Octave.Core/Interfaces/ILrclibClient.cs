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
}
