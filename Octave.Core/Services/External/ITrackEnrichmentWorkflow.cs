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

    // ENR-02 companion: lazily fills a candidate's artwork and lyrics comparisons
    // when the user selects it in the review dialog (plans hydrate only the top
    // candidate up front).
    Task<CandidatePreview> HydrateCandidateAsync(
        Track track,
        CandidatePreview preview,
        CancellationToken ct = default);

    Task<EnrichmentApplyResult> ApplyEnrichmentAsync(
        EnrichmentApplyRequest request,
        CancellationToken ct = default);
}
