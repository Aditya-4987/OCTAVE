using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Octave.Core.Helpers;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.External;
using Octave.Core.Services.Library;

namespace Octave_Desktop.ViewModels;

public partial class MetadataEnrichmentViewModel : ObservableObject
{
    private readonly ITrackEnrichmentWorkflow _workflow;
    private readonly ILibraryService _libraryService;
    private readonly IArtworkCacheManager _artworkCacheManager;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    private Track? _track;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _applyCts;

    // ENR-02: candidate ids whose artwork/lyrics comparisons are already filled.
    // The plan hydrates only its top candidate up front; every other candidate is
    // hydrated lazily here when the user selects it in the ComboBox.
    private readonly HashSet<string> _hydratedCandidateIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _hydrateCts;

    [ObservableProperty]
    public partial string SearchTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchArtist { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchAlbum { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial bool IsApplying { get; set; }

    [ObservableProperty]
    public partial bool IsStatusVisible { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial MatchConfidenceTier ConfidenceTier { get; set; } = MatchConfidenceTier.NoMatch;

    [ObservableProperty]
    public partial string ConfidenceBadgeText { get; set; } = "No Match";

    [ObservableProperty]
    public partial bool HasCandidates { get; set; }

    [ObservableProperty]
    public partial CandidatePreview? SelectedCandidate { get; set; }

    // Field-level selection flags
    [ObservableProperty]
    public partial bool ApplyTitle { get; set; } = true;

    [ObservableProperty]
    public partial bool ApplyArtist { get; set; } = true;

    [ObservableProperty]
    public partial bool ApplyAlbum { get; set; } = true;

    [ObservableProperty]
    public partial bool ApplyAlbumArtist { get; set; } = true;

    [ObservableProperty]
    public partial bool ApplyGenre { get; set; } = true;

    [ObservableProperty]
    public partial bool ApplyYear { get; set; } = true;

    [ObservableProperty]
    public partial bool ApplyTrackNumber { get; set; } = true;

    [ObservableProperty]
    public partial bool ApplyDiscNumber { get; set; } = true;

    [ObservableProperty]
    public partial bool ApplyLyrics { get; set; } = true;

    [ObservableProperty]
    public partial bool ApplyArtwork { get; set; } = true;

    [ObservableProperty]
    public partial bool ApplyExternalIds { get; set; } = true;

    // Custom local artwork replacement
    [ObservableProperty]
    public partial string? CustomArtworkPath { get; set; }

    [ObservableProperty]
    public partial byte[]? CustomArtworkBytes { get; set; }

    public ObservableCollection<CandidatePreview> Candidates { get; } = new();

    public Track? CurrentTrack => _track;

    public MetadataEnrichmentViewModel(
        ITrackEnrichmentWorkflow workflow,
        ILibraryService libraryService,
        IArtworkCacheManager artworkCacheManager)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _artworkCacheManager = artworkCacheManager ?? throw new ArgumentNullException(nameof(artworkCacheManager));
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    }

    public async Task InitializeAsync(Track track)
    {
        _track = track ?? throw new ArgumentNullException(nameof(track));

        SearchTitle = track.Title;
        SearchArtist = track.ArtistName;
        SearchAlbum = track.AlbumTitle;

        CustomArtworkPath = null;
        CustomArtworkBytes = null;

        await RunSearchAsync();
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        if (_track == null) return;
        await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        if (_track == null) return;

        // VM-05: cancel AND dispose the previous source - cancelling alone
        // leaked its wait handle on every re-search.
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsSearching = true;
        IsStatusVisible = false;
        StatusMessage = "Searching online metadata providers...";
        StatusSeverity = InfoBarSeverity.Informational;

        try
        {
            var searchTrack = new Track(
                _track.Id,
                SearchTitle,
                _track.ArtistId,
                SearchArtist,
                _track.AlbumId,
                SearchAlbum,
                _track.DurationSeconds,
                _track.SourceUri,
                _track.Provider,
                _track.TrackNumber,
                _track.Year,
                _track.DateAdded,
                _track.Genre,
                _track.ReplayGain);

            var plan = await Task.Run(() => _workflow.CreateEnrichmentPlanAsync(searchTrack, ct), ct);

            _dispatcher.TryEnqueue(() =>
            {
                Candidates.Clear();
                foreach (var c in plan.Candidates)
                {
                    Candidates.Add(c);
                }

                // ENR-02: the new plan's top candidate arrives pre-hydrated by
                // CreateEnrichmentPlanAsync; everything else must re-hydrate on
                // selection, so the per-plan tracking set starts over.
                _hydratedCandidateIds.Clear();
                if (plan.BestCandidate != null)
                {
                    _hydratedCandidateIds.Add(plan.BestCandidate.CandidateId);
                }

                HasCandidates = Candidates.Count > 0;
                ConfidenceTier = plan.TopConfidenceTier;

                ConfidenceBadgeText = plan.TopConfidenceTier switch
                {
                    MatchConfidenceTier.ExactMatch => $"Exact Match ({Math.Round((plan.BestCandidate?.Confidence ?? 1.0) * 100)}%)",
                    MatchConfidenceTier.ProbableMatch => $"Probable Match ({Math.Round((plan.BestCandidate?.Confidence ?? 0.75) * 100)}%)",
                    MatchConfidenceTier.AmbiguousMatch => $"Ambiguous Match ({Math.Round((plan.BestCandidate?.Confidence ?? 0.5) * 100)}%)",
                    _ => "No Matches Found"
                };

                SelectedCandidate = plan.BestCandidate;
                if (SelectedCandidate != null)
                {
                    UpdateFieldSelectionDefaults(SelectedCandidate);
                }

                IsSearching = false;
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.TryEnqueue(() => IsSearching = false);
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() =>
            {
                IsSearching = false;
                IsStatusVisible = true;
                StatusSeverity = InfoBarSeverity.Error;
                StatusMessage = $"Metadata search failed: {ex.Message}";
            });
        }
    }

    partial void OnSelectedCandidateChanged(CandidatePreview? value)
    {
        if (value != null)
        {
            UpdateFieldSelectionDefaults(value);
            BeginHydrateCandidate(value);
        }
    }

    // ENR-02: switching to a candidate the plan did NOT pre-hydrate used to leave
    // its artwork/lyrics comparisons permanently empty ("-" rows and no cover
    // preview) even though HydrateCandidateAsync existed for exactly this. Kick
    // off a detached hydration whenever an unhydrated candidate is selected.
    private void BeginHydrateCandidate(CandidatePreview candidate)
    {
        if (_track == null || _hydratedCandidateIds.Contains(candidate.CandidateId))
        {
            return;
        }

        // VM-05 pattern: cancel AND dispose the superseded source. A newer
        // selection always wins; its result swaps into Candidates when it lands.
        _hydrateCts?.Cancel();
        _hydrateCts?.Dispose();
        var cts = new CancellationTokenSource();
        _hydrateCts = cts;

        var track = _track;
        _ = HydrateCandidateAsyncCore(track, candidate, cts, cts.Token);
    }

    private async Task HydrateCandidateAsyncCore(
        Track track,
        CandidatePreview candidate,
        CancellationTokenSource cts,
        CancellationToken ct)
    {
        try
        {
            var hydrated = await Task.Run(() => _workflow.HydrateCandidateAsync(track, candidate, ct), ct);

            // A re-selection or dialog close owns the state now - drop this run.
            if (!ReferenceEquals(cts, _hydrateCts)) return;

            _dispatcher.TryEnqueue(() =>
            {
                if (!ReferenceEquals(cts, _hydrateCts)) return;

                // Mark BEFORE re-pointing SelectedCandidate: assigning below raises
                // OnSelectedCandidateChanged again, which must see the id as done.
                _hydratedCandidateIds.Add(hydrated.CandidateId);

                int idx = Candidates.IndexOf(candidate);
                if (idx >= 0)
                {
                    Candidates[idx] = hydrated;
                }

                if (ReferenceEquals(SelectedCandidate, candidate))
                {
                    SelectedCandidate = hydrated;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection or dialog close.
        }
        catch (Exception ex)
        {
            // Deliberately NOT marked hydrated so picking the candidate again
            // retries (e.g. a transient provider/network failure).
            System.Diagnostics.Debug.WriteLine(
                $"[MetadataEnrichmentViewModel] Candidate hydration failed for '{candidate.CandidateId}': {ex.Message}");
        }
    }

    private void UpdateFieldSelectionDefaults(CandidatePreview c)
    {
        ApplyTitle = c.Title.HasDifference;
        ApplyArtist = c.Artist.HasDifference;
        ApplyAlbum = c.Album.HasDifference;
        ApplyAlbumArtist = c.AlbumArtist.HasDifference;
        ApplyGenre = c.Genre.HasDifference;
        ApplyYear = c.Year.HasDifference;
        ApplyTrackNumber = c.TrackNumber.HasDifference;
        ApplyDiscNumber = c.DiscNumber.HasDifference;
        ApplyLyrics = c.Lyrics.HasDifference;
        ApplyArtwork = c.ArtworkUrl.HasDifference || c.ProposedArtworkBytes != null;
        ApplyExternalIds = c.ExternalIds.HasDifference;
    }

    public async Task<bool> ApplyChangesAsync()
    {
        if (_track == null || SelectedCandidate == null) return false;

        // ME-06/VM-05: the previous source was neither cancelled nor disposed on
        // re-entry, so rapid re-clicks stacked concurrent ApplyEnrichmentAsync
        // runs writing the SAME audio file. Cancelling first serializes applies;
        // disposing releases the old handle.
        _applyCts?.Cancel();
        _applyCts?.Dispose();
        var cts = new CancellationTokenSource();
        _applyCts = cts;
        var ct = cts.Token;

        IsApplying = true;
        IsStatusVisible = true;
        StatusSeverity = InfoBarSeverity.Informational;
        StatusMessage = "Writing metadata tags to audio file...";

        try
        {
            // If custom artwork was loaded from file, inject into candidate
            var candidateToApply = SelectedCandidate;
            if (CustomArtworkBytes != null && CustomArtworkBytes.Length > 0)
            {
                candidateToApply = SelectedCandidate with
                {
                    ProposedArtworkBytes = CustomArtworkBytes,
                    ProposedArtworkMime = ImageValidator.IsValidImage(CustomArtworkBytes, out var mime) ? mime : "image/jpeg"
                };
            }

            var selection = new FieldSelectionOptions(
                ApplyTitle: ApplyTitle,
                ApplyArtist: ApplyArtist,
                ApplyAlbum: ApplyAlbum,
                ApplyAlbumArtist: ApplyAlbumArtist,
                // ME-07: Composer/TrackCount/DiscCount have no UI binding and are
                // never shown in the dialog - force-applying them (the record's
                // default-true) wrote fields the user could neither see nor
                // approve. Opt-out until real toggles ship with the VM/UI batch.
                ApplyComposer: false,
                ApplyTrackCount: false,
                ApplyDiscCount: false,
                ApplyGenre: ApplyGenre,
                ApplyYear: ApplyYear,
                ApplyTrackNumber: ApplyTrackNumber,
                ApplyDiscNumber: ApplyDiscNumber,
                ApplyLyrics: ApplyLyrics,
                ApplyArtwork: ApplyArtwork || CustomArtworkBytes != null,
                ApplyExternalIds: ApplyExternalIds);

            var req = new EnrichmentApplyRequest(_track.Id, candidateToApply, selection);

            var result = await Task.Run(() => _workflow.ApplyEnrichmentAsync(req, ct), ct);

            // VM-13: a newer apply owns the dialog state now - this stale run
            // must not write anything.
            if (!ReferenceEquals(cts, _applyCts)) return false;

            bool success = result.Success;
            string message = success
                ? $"Metadata updated successfully! Applied: {string.Join(", ", result.AppliedFields)}"
                : result.ErrorMessage ?? result.EditResult.SummaryMessage ?? "Metadata edit failed.";

            // VM-13: mutate observables on the UI thread like RunSearchAsync does.
            _dispatcher.TryEnqueue(() =>
            {
                IsApplying = false;
                IsStatusVisible = true;
                StatusSeverity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
                StatusMessage = message;
            });

            if (success)
            {
                // Notify library of changes to update all open views
                _libraryService.NotifyLibraryUpdated();
            }
            return success;
        }
        catch (OperationCanceledException)
        {
            // VM-13: a cancelled apply is superseded work - never write success
            // or failure state over whatever the newer run is showing.
            if (ReferenceEquals(cts, _applyCts))
            {
                _dispatcher.TryEnqueue(() => IsApplying = false);
            }
            return false;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(cts, _applyCts))
            {
                _dispatcher.TryEnqueue(() =>
                {
                    IsApplying = false;
                    IsStatusVisible = true;
                    StatusSeverity = InfoBarSeverity.Error;
                    StatusMessage = $"Application failed: {ex.Message}";
                });
            }
            return false;
        }
    }

    // VM-05: invoked when the enrichment dialog closes. Cancels any in-flight
    // search/apply and disposes both sources so nothing outlives the dialog -
    // previously the CTSs leaked and an apply could keep writing after close.
    public void Cleanup()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        _applyCts?.Cancel();
        _applyCts?.Dispose();
        _applyCts = null;

        // ENR-02: an in-flight candidate hydration must not swap into a closed dialog.
        _hydrateCts?.Cancel();
        _hydrateCts?.Dispose();
        _hydrateCts = null;
    }

    public void SetCustomArtwork(string filePath, byte[] bytes)
    {
        CustomArtworkPath = filePath;
        CustomArtworkBytes = bytes;
        ApplyArtwork = true;
    }
}
