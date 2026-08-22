using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Services.Metadata;

public interface ITrackMetadataEditor
{
    Task<MetadataEditResult> UpdateTrackMetadataAsync(
        string trackId,
        TrackMetadataUpdate update,
        CancellationToken ct = default);
}
