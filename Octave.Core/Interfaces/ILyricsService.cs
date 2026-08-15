using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Interfaces;

public interface ILyricsService
{
    Task<LyricsData> GetLyricsAsync(Track track, CancellationToken cancellationToken = default);
}
