using System;

namespace Octave.Core.Models;

public enum MetadataWritePolicy
{
    NeverWriteAutomatically = 0,
    WriteOnlyWhenMissingAndHighConfidence = 1, // Recommended Safe Default
    WriteOnlyHighConfidence = 2,
    AlwaysPreferOnline = 3
}

public class ExternalDataSettings
{
    // Master & Feature Toggles
    public bool EnableOnlineMetadata { get; set; } = true;
    public bool EnableOnlineLyrics { get; set; } = true;
    public bool EnableOnlineArtwork { get; set; } = true;
    public bool EnableArtistEnrichment { get; set; } = true;
    public bool OfflineOnlyMode { get; set; } = false;

    // Provider Toggles
    public bool MusicBrainzEnabled { get; set; } = true;
    public bool LrcLibEnabled { get; set; } = true;
    public bool CoverArtArchiveEnabled { get; set; } = true;
    public bool TheAudioDbEnabled { get; set; } = true;

    // API Credentials
    public string TheAudioDbApiKey { get; set; } = "2";

    // Caching & Network
    public bool UseCachedDataOffline { get; set; } = true;
    public bool AllowStaleCacheOnProviderFailure { get; set; } = true;
    public int MaxConcurrentRequests { get; set; } = 4;
    public int CacheRetentionDays { get; set; } = 30;

    // Automatic Enrichment & Write Policy
    public bool AutoDownloadMissingArtwork { get; set; } = true;
    public bool AutoDownloadMissingArtistImages { get; set; } = true;
    public bool AutoFillMissingMetadata { get; set; } = false;
    public bool ReplaceExistingMetadata { get; set; } = false;
    public bool ReplaceExistingArtwork { get; set; } = false;
    public bool WriteExternalIdsToTags { get; set; } = true;
    public MetadataWritePolicy WritePolicy { get; set; } = MetadataWritePolicy.WriteOnlyWhenMissingAndHighConfidence;

    // Online Scan Policy Preferences
    public bool ScanEntireLibrary { get; set; } = false;
    public bool ScanOnlyMissingMetadata { get; set; } = true;
    public bool ScanOnlyMissingArtwork { get; set; } = true;
    public bool ScanOnlyMissingLyrics { get; set; } = true;
    public bool RecheckPreviouslyFailedMatches { get; set; } = false;
    public bool RecheckAmbiguousMatches { get; set; } = false;

    public ExternalDataSettings Clone()
    {
        return (ExternalDataSettings)this.MemberwiseClone();
    }
}

public record TestConnectionResult(bool Success, string Message, int? StatusCode = null);
