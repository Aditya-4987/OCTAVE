using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Models;
using Octave.Core.Services.External;

namespace Octave_Desktop.ViewModels;

public partial class ReviewItemViewModel : ObservableObject
{
    private readonly ISmartLibraryEnrichmentService _enrichmentService;

    public TrackEnrichmentExecutionPlan Plan { get; }

    public string TrackId => Plan.TrackId;
    public string LocalTitle => Plan.LocalTitle;
    public string LocalArtist => Plan.LocalArtist;
    public string LocalAlbum => Plan.LocalAlbum;
    public string LocalDurationText => TimeSpan.FromSeconds(Plan.LocalDurationSeconds).ToString(@"m\:ss");
    public int LocalYear => Plan.LocalYear;

    public string ProposedTitle => Plan.SelectedCandidate?.Metadata.Title ?? "—";
    public string ProposedArtist => Plan.SelectedCandidate?.Metadata.ArtistName ?? "—";
    public string ProposedAlbum => Plan.SelectedCandidate?.Metadata.AlbumTitle ?? "—";
    public string ProposedDurationText => Plan.SelectedCandidate?.Metadata.DurationSeconds.HasValue == true
        ? TimeSpan.FromSeconds(Plan.SelectedCandidate.Metadata.DurationSeconds.Value).ToString(@"m\:ss")
        : "—";
    public string ProposedYear => Plan.SelectedCandidate?.Metadata.Year?.ToString() ?? "—";

    public double Confidence => Plan.Confidence;
    public string ConfidencePercentageText => $"{(Plan.Confidence * 100):0}% Match";

    public IReadOnlyList<string> PositiveEvidence => Plan.SafetyGates.PositiveEvidence;
    public IReadOnlyList<string> Warnings => Plan.SafetyGates.Warnings;
    public IReadOnlyList<TrackMatchCandidate> Alternatives => Plan.AlternativeCandidates;

    [ObservableProperty]
    public partial bool IsProcessing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotResolved))]
    public partial bool IsResolved { get; set; }

    public bool IsNotResolved => !IsResolved;

    public string FormattedLocalSummary
    {
        get
        {
            string y = LocalYear > 0 ? $" ({LocalYear})" : "";
            string alb = !string.IsNullOrWhiteSpace(LocalAlbum) ? $" · {LocalAlbum}{y}" : "";
            return $"{LocalTitle} · {LocalArtist}{alb}";
        }
    }

    public string FormattedProposedSummary
    {
        get
        {
            string y = ProposedYear != "—" && !string.IsNullOrWhiteSpace(ProposedYear) ? $" ({ProposedYear})" : "";
            string alb = ProposedAlbum != "—" && !string.IsNullOrWhiteSpace(ProposedAlbum) ? $" · {ProposedAlbum}{y}" : "";
            return $"{ProposedTitle} · {ProposedArtist}{alb}";
        }
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    public ReviewItemViewModel(TrackEnrichmentExecutionPlan plan, ISmartLibraryEnrichmentService enrichmentService)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _enrichmentService = enrichmentService ?? throw new ArgumentNullException(nameof(enrichmentService));
    }

    [RelayCommand]
    public async Task ApplyCandidateAsync()
    {
        IsProcessing = true;
        try
        {
            var result = await _enrichmentService.ApplySinglePlanAsync(Plan);
            IsResolved = true;
            StatusText = result.Success ? "Applied Successfully" : "Apply Failed";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    public async Task SkipAsync()
    {
        IsProcessing = true;
        try
        {
            await _enrichmentService.RejectOrSkipTrackAsync(TrackId, neverAskAgain: false);
            IsResolved = true;
            StatusText = "Skipped";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    public async Task NeverAskAgainAsync()
    {
        IsProcessing = true;
        try
        {
            await _enrichmentService.RejectOrSkipTrackAsync(TrackId, neverAskAgain: true);
            IsResolved = true;
            StatusText = "Excluded (Never Ask Again)";
        }
        finally
        {
            IsProcessing = false;
        }
    }
}

public partial class LibraryEnrichmentViewModel : ObservableObject
{
    private readonly ISmartLibraryEnrichmentService _enrichmentService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    [ObservableProperty]
    public partial bool IsDryRun { get; set; } = true;

    [ObservableProperty]
    public partial double ProgressPercentage { get; set; }

    [ObservableProperty]
    public partial string CurrentTrackTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentOperation { get; set; } = "Ready to scan";

    [ObservableProperty]
    public partial int TotalTracks { get; set; }

    [ObservableProperty]
    public partial int ScannedTracks { get; set; }

    [ObservableProperty]
    public partial int SafeReadyCount { get; set; }

    [ObservableProperty]
    public partial int EnrichedCount { get; set; }

    [ObservableProperty]
    public partial int AlreadyCompleteCount { get; set; }

    [ObservableProperty]
    public partial int NeedsReviewCount { get; set; }

    [ObservableProperty]
    public partial int NoMatchCount { get; set; }

    [ObservableProperty]
    public partial int FailedCount { get; set; }

    [ObservableProperty]
    public partial int MetadataUpdatedCount { get; set; }

    [ObservableProperty]
    public partial int ArtworkAddedCount { get; set; }

    [ObservableProperty]
    public partial int ArtistsEnrichedCount { get; set; }

    [ObservableProperty]
    public partial int LyricsAddedCount { get; set; }

    [ObservableProperty]
    public partial bool HasReviewItems { get; set; }

    [ObservableProperty]
    public partial string ElapsedDurationText { get; set; } = "00:00";

    [ObservableProperty]
    public partial ObservableCollection<ReviewItemViewModel> ReviewQueue { get; set; } = new();

    public LibraryEnrichmentViewModel(ISmartLibraryEnrichmentService enrichmentService)
    {
        _enrichmentService = enrichmentService ?? throw new ArgumentNullException(nameof(enrichmentService));
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _enrichmentService.ProgressChanged += OnProgressChanged;
        _enrichmentService.ScanCompleted += OnScanCompleted;
    }

    [RelayCommand]
    public async Task StartPreviewDryRunAsync()
    {
        IsDryRun = true;
        IsScanning = true;
        IsCompleted = false;
        ReviewQueue.Clear();

        await _enrichmentService.RunEnrichmentScanAsync(dryRun: true);
    }

    [RelayCommand]
    public async Task ApplySafeChangesAsync()
    {
        IsDryRun = false;
        IsScanning = true;
        IsCompleted = false;

        await _enrichmentService.RunEnrichmentScanAsync(dryRun: false);
    }

    [RelayCommand]
    public void CancelScan()
    {
        _enrichmentService.CancelScan();
        IsScanning = false;
        CurrentOperation = "Scan cancelled by user.";
    }

    [RelayCommand]
    public async Task LoadReviewQueueAsync()
    {
        var plans = await _enrichmentService.GetReviewQueueAsync();
        ReviewQueue.Clear();
        foreach (var p in plans)
        {
            ReviewQueue.Add(new ReviewItemViewModel(p, _enrichmentService));
        }
        HasReviewItems = ReviewQueue.Count > 0;
    }

    private void OnProgressChanged(object? sender, LibraryEnrichmentProgress e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            TotalTracks = e.TotalTracks;
            ScannedTracks = e.ScannedTracks;
            SafeReadyCount = e.SafeReadyCount;
            EnrichedCount = e.EnrichedCount;
            AlreadyCompleteCount = e.AlreadyCompleteCount;
            NeedsReviewCount = e.NeedsReviewCount;
            NoMatchCount = e.NoMatchCount;
            FailedCount = e.FailedCount;
            CurrentTrackTitle = e.CurrentTrackTitle;
            CurrentOperation = e.CurrentOperation;
            ProgressPercentage = e.Percentage;
            IsScanning = e.IsRunning;
        });
    }

    private void OnScanCompleted(object? sender, LibraryEnrichmentSummary summary)
    {
        _dispatcher.TryEnqueue(() =>
        {
            IsScanning = false;
            IsCompleted = true;
            MetadataUpdatedCount = summary.MetadataFieldsUpdated;
            ArtworkAddedCount = summary.AlbumArtworkAdded;
            ArtistsEnrichedCount = summary.ArtistEnrichmentsAdded;
            LyricsAddedCount = summary.LyricsAdded;
            ElapsedDurationText = summary.ElapsedDuration.ToString(@"mm\:ss");

            ReviewQueue.Clear();
            var reviewPlans = summary.ExecutionPlans.Where(p => p.Status == EnrichmentTrackStatus.NeedsReview);
            foreach (var p in reviewPlans)
            {
                ReviewQueue.Add(new ReviewItemViewModel(p, _enrichmentService));
            }
            HasReviewItems = ReviewQueue.Count > 0;
        });
    }
}
