using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Services.External;

public interface ITrackEnrichmentWorkflow
{
    Task<TrackEnrichmentPlan> CreateEnrichmentPlanAsync(
        Track track,
        CancellationToken ct = default);

    Task<TrackEnrichmentPlan> CreateEnrichmentPlanForTrackIdAsync(
        string trackId,
        CancellationToken ct = default);

    Task<EnrichmentApplyResult> ApplyEnrichmentAsync(
        EnrichmentApplyRequest request,
        CancellationToken ct = default);
}
