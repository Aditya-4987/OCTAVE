using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Octave.Core.Models;
using Octave.Core.Services.External.Settings;

namespace Octave_Desktop.ViewModels;

public partial class ExternalDataSettingsViewModel : ObservableObject
{
    private readonly IExternalDataSettingsService _settingsService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private CancellationTokenSource? _testCts;

    // VM-01: the handler is kept in a field so Cleanup() can detach it - the
    // old inline lambda made unsubscribing impossible, leaking one VM (and its
    // dispatcher closure) per visit to the Settings page.
    private readonly EventHandler<ExternalDataSettings> _settingsChangedHandler;

    // Master & Feature Toggles
    [ObservableProperty]
    public partial bool EnableOnlineMetadata { get; set; } = true;

    [ObservableProperty]
    public partial bool EnableOnlineLyrics { get; set; } = true;

    [ObservableProperty]
    public partial bool EnableOnlineArtwork { get; set; } = true;

    [ObservableProperty]
    public partial bool EnableArtistEnrichment { get; set; } = true;

    [ObservableProperty]
    public partial bool OfflineOnlyMode { get; set; }

    // Provider Toggles
    [ObservableProperty]
    public partial bool MusicBrainzEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool LrcLibEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool CoverArtArchiveEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool TheAudioDbEnabled { get; set; } = true;

    // API Credentials
    [ObservableProperty]
    public partial string TheAudioDbApiKey { get; set; } = "2";

    [ObservableProperty]
    public partial string TheAudioDbStatusText { get; set; } = "Configured (Default Test Key '2')";

    [ObservableProperty]
    public partial bool IsTestingConnection { get; set; }

    [ObservableProperty]
    public partial bool IsConnectionTestVisible { get; set; }

    [ObservableProperty]
    public partial string ConnectionTestMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity ConnectionTestSeverity { get; set; } = InfoBarSeverity.Informational;

    // Caching & Network
    [ObservableProperty]
    public partial bool UseCachedDataOffline { get; set; } = true;

    [ObservableProperty]
    public partial bool AllowStaleCacheOnProviderFailure { get; set; } = true;

    [ObservableProperty]
    public partial int MaxConcurrentRequests { get; set; } = 4;

    [ObservableProperty]
    public partial int CacheRetentionDaysIndex { get; set; } = 1; // 0=7d, 1=30d, 2=90d, 3=180d, 4=365d

    // Automation & Write Policy
    [ObservableProperty]
    public partial bool AutoDownloadMissingArtwork { get; set; } = true;

    [ObservableProperty]
    public partial bool AutoDownloadMissingArtistImages { get; set; } = true;

    [ObservableProperty]
    public partial bool AutoFillMissingMetadata { get; set; }

    [ObservableProperty]
    public partial bool ReplaceExistingMetadata { get; set; }

    [ObservableProperty]
    public partial bool ReplaceExistingArtwork { get; set; }

    [ObservableProperty]
    public partial bool WriteExternalIdsToTags { get; set; } = true;

    [ObservableProperty]
    public partial int WritePolicyIndex { get; set; } = 1; // 1 = WriteOnlyWhenMissingAndHighConfidence

    // Scan Policy Preferences
    [ObservableProperty]
    public partial bool ScanEntireLibrary { get; set; }

    [ObservableProperty]
    public partial bool ScanOnlyMissingMetadata { get; set; } = true;

    [ObservableProperty]
    public partial bool ScanOnlyMissingArtwork { get; set; } = true;

    [ObservableProperty]
    public partial bool ScanOnlyMissingLyrics { get; set; } = true;

    [ObservableProperty]
    public partial bool RecheckPreviouslyFailedMatches { get; set; }

    [ObservableProperty]
    public partial bool RecheckAmbiguousMatches { get; set; }

    public ExternalDataSettingsViewModel(IExternalDataSettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        LoadFromSettings(_settingsService.CurrentSettings);
        _settingsChangedHandler = (s, settings) =>
        {
            _dispatcher.TryEnqueue(() => LoadFromSettings(settings));
        };
        _settingsService.SettingsChanged += _settingsChangedHandler;
    }

    // VM-01: invoked from SettingsPage.Unloaded - detaches the singleton's
    // event and disposes any in-flight connection test so the transient VM can
    // actually be collected on navigation away.
    public void Cleanup()
    {
        _settingsService.SettingsChanged -= _settingsChangedHandler;

        _testCts?.Cancel();
        _testCts?.Dispose();
        _testCts = null;
    }

    public async Task InitializeAsync()
    {
        await _settingsService.LoadSettingsAsync();
        LoadFromSettings(_settingsService.CurrentSettings);
    }

    private void LoadFromSettings(ExternalDataSettings s)
    {
        EnableOnlineMetadata = s.EnableOnlineMetadata;
        EnableOnlineLyrics = s.EnableOnlineLyrics;
        EnableOnlineArtwork = s.EnableOnlineArtwork;
        EnableArtistEnrichment = s.EnableArtistEnrichment;
        OfflineOnlyMode = s.OfflineOnlyMode;

        MusicBrainzEnabled = s.MusicBrainzEnabled;
        LrcLibEnabled = s.LrcLibEnabled;
        CoverArtArchiveEnabled = s.CoverArtArchiveEnabled;
        TheAudioDbEnabled = s.TheAudioDbEnabled;

        TheAudioDbApiKey = s.TheAudioDbApiKey;
        UpdateTheAudioDbStatusText(s.TheAudioDbApiKey);

        UseCachedDataOffline = s.UseCachedDataOffline;
        AllowStaleCacheOnProviderFailure = s.AllowStaleCacheOnProviderFailure;
        MaxConcurrentRequests = s.MaxConcurrentRequests;

        CacheRetentionDaysIndex = s.CacheRetentionDays switch
        {
            7 => 0,
            30 => 1,
            90 => 2,
            180 => 3,
            365 => 4,
            _ => 1
        };

        AutoDownloadMissingArtwork = s.AutoDownloadMissingArtwork;
        AutoDownloadMissingArtistImages = s.AutoDownloadMissingArtistImages;
        AutoFillMissingMetadata = s.AutoFillMissingMetadata;
        ReplaceExistingMetadata = s.ReplaceExistingMetadata;
        ReplaceExistingArtwork = s.ReplaceExistingArtwork;
        WriteExternalIdsToTags = s.WriteExternalIdsToTags;
        WritePolicyIndex = (int)s.WritePolicy;

        ScanEntireLibrary = s.ScanEntireLibrary;
        ScanOnlyMissingMetadata = s.ScanOnlyMissingMetadata;
        ScanOnlyMissingArtwork = s.ScanOnlyMissingArtwork;
        ScanOnlyMissingLyrics = s.ScanOnlyMissingLyrics;
        RecheckPreviouslyFailedMatches = s.RecheckPreviouslyFailedMatches;
        RecheckAmbiguousMatches = s.RecheckAmbiguousMatches;
    }

    private void UpdateTheAudioDbStatusText(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            TheAudioDbStatusText = "Not Configured (Disabled)";
        }
        else if (key.Trim() == "2")
        {
            TheAudioDbStatusText = "Configured (Default Public Test Key '2')";
        }
        else
        {
            TheAudioDbStatusText = "Configured (Personal Custom Key)";
        }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        int retentionDays = CacheRetentionDaysIndex switch
        {
            0 => 7,
            1 => 30,
            2 => 90,
            3 => 180,
            4 => 365,
            _ => 30
        };

        var settings = new ExternalDataSettings
        {
            EnableOnlineMetadata = EnableOnlineMetadata,
            EnableOnlineLyrics = EnableOnlineLyrics,
            EnableOnlineArtwork = EnableOnlineArtwork,
            EnableArtistEnrichment = EnableArtistEnrichment,
            OfflineOnlyMode = OfflineOnlyMode,

            MusicBrainzEnabled = MusicBrainzEnabled,
            LrcLibEnabled = LrcLibEnabled,
            CoverArtArchiveEnabled = CoverArtArchiveEnabled,
            TheAudioDbEnabled = TheAudioDbEnabled,

            TheAudioDbApiKey = TheAudioDbApiKey?.Trim() ?? string.Empty,

            UseCachedDataOffline = UseCachedDataOffline,
            AllowStaleCacheOnProviderFailure = AllowStaleCacheOnProviderFailure,
            MaxConcurrentRequests = MaxConcurrentRequests,
            CacheRetentionDays = retentionDays,

            AutoDownloadMissingArtwork = AutoDownloadMissingArtwork,
            AutoDownloadMissingArtistImages = AutoDownloadMissingArtistImages,
            AutoFillMissingMetadata = AutoFillMissingMetadata,
            ReplaceExistingMetadata = ReplaceExistingMetadata,
            ReplaceExistingArtwork = ReplaceExistingArtwork,
            WriteExternalIdsToTags = WriteExternalIdsToTags,
            WritePolicy = (MetadataWritePolicy)WritePolicyIndex,

            ScanEntireLibrary = ScanEntireLibrary,
            ScanOnlyMissingMetadata = ScanOnlyMissingMetadata,
            ScanOnlyMissingArtwork = ScanOnlyMissingArtwork,
            ScanOnlyMissingLyrics = ScanOnlyMissingLyrics,
            RecheckPreviouslyFailedMatches = RecheckPreviouslyFailedMatches,
            RecheckAmbiguousMatches = RecheckAmbiguousMatches
        };

        UpdateTheAudioDbStatusText(settings.TheAudioDbApiKey);
        await _settingsService.UpdateSettingsAsync(settings);
    }

    [RelayCommand]
    public async Task TestTheAudioDbConnectionAsync()
    {
        // VM-09: dispose the replaced source, not just cancel it.
        _testCts?.Cancel();
        _testCts?.Dispose();
        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;

        IsTestingConnection = true;
        IsConnectionTestVisible = true;
        ConnectionTestSeverity = InfoBarSeverity.Informational;
        ConnectionTestMessage = "Testing connection to TheAudioDB...";

        try
        {
            var result = await _settingsService.TestTheAudioDbConnectionAsync(TheAudioDbApiKey, ct);

            _dispatcher.TryEnqueue(() =>
            {
                IsTestingConnection = false;
                ConnectionTestMessage = result.Message;
                ConnectionTestSeverity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.TryEnqueue(() => IsTestingConnection = false);
        }
        catch (Exception)
        {
            _dispatcher.TryEnqueue(() =>
            {
                IsTestingConnection = false;
                ConnectionTestSeverity = InfoBarSeverity.Error;
                ConnectionTestMessage = "Connection failed. Please check your network and API key.";
            });
        }
    }
}
