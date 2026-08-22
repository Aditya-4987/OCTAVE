using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Services.External;

public interface ISmartLibraryEnrichmentService
{
    event EventHandler<LibraryEnrichmentProgress>? ProgressChanged;
    event EventHandler<LibraryEnrichmentSummary>? ScanCompleted;

    Task<LibraryEnrichmentSummary> RunEnrichmentScanAsync(bool dryRun, CancellationToken ct = default);
    Task<IReadOnlyList<TrackEnrichmentExecutionPlan>> GetReviewQueueAsync(CancellationToken ct = default);
    Task<EnrichmentApplyResult> ApplySinglePlanAsync(TrackEnrichmentExecutionPlan plan, CancellationToken ct = default);
    Task<int> ApplySafePlansAsync(IEnumerable<TrackEnrichmentExecutionPlan> plans, CancellationToken ct = default);
    Task RejectOrSkipTrackAsync(string trackId, bool neverAskAgain, CancellationToken ct = default);
    void CancelScan();
    Task ResetSessionStateAsync(CancellationToken ct = default);
}
