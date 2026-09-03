using System;

namespace Octave.Core.Models;

// =================================================================
// 1. PERSISTED ENTITIES (Mapped 1:1 to SQLite STRICT Tables)
// =================================================================

public record Artist(
    string Id,
    string Name,
    string? Bio = null,
    string? ArtworkUrl = null
);

public record ArtistDisplayItem(string? Id, string Name, string? ArtworkUrl);

public record Album(
    string Id,
    string Title,
    string ArtistId,
    string ArtistName,
    int Year,
    string? ArtworkUrl = null
);

public record Track(
    string Id,
    string Title,
    string ArtistId,
    string ArtistName,
    string AlbumId,
    string AlbumTitle,
    double DurationSeconds,
    string SourceUri,       // Local absolute file path
    int TrackNumber,
    int Year,
    DateTime DateAdded,
    string Genre = "",      // Trailing default so existing constructors are unaffected
    float ReplayGain = 0.0f, // Applied in playback to normalize loudness
    int DiscNumber = 1      // SCAN-11: multi-disc albums order by (Disc, Track); 1 when the tag is absent
);

public record Playlist(
    string Id,
    string Title,
    string? Description,
    DateTime CreatedAt,
    int TrackCount = 0      // Hydrated dynamically by SQL COUNT()
);

public record PlaylistTrackEntry(
    string EntryId,
    string PlaylistId,
    Track Track,
    int SortOrder
);

// =================================================================
// 2. RUNTIME / STATE MODELS (Never saved to SQL directly)
// =================================================================

public class QueueItem : System.ComponentModel.INotifyPropertyChanged
{
    private static Action<Action>? s_uiDispatcher;

    /// <summary>
    /// Configures an ambient UI thread dispatcher for QueueItem property change notifications.
    /// Ensures WinUI 3 XAML data bindings receive PropertyChanged notifications on the UI STA thread,
    /// preventing WinRT RPC_E_WRONG_THREAD (0x8001010E) COM exceptions during background auto-advances.
    /// </summary>
    public static void SetUIDispatcher(Action<Action>? dispatcher)
    {
        s_uiDispatcher = dispatcher;
    }

    private bool _isPlaying;
    private string? _artworkUrl;

    public required string Id { get; init; }
    public required Track Track { get; init; }

    // NP-20/NP-21: QueueService mutates this flag as playback advances. Raising
    // PropertyChanged lets the queue row templates ({x:Bind ..., Mode=OneWay})
    // move the playing highlight and glyph without a full list rebuild.
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying != value)
            {
                _isPlaying = value;
                OnPropertyChanged();
            }
        }
    }

    // NP-19: the album artwork token for the queue-row thumbnail. Resolved
    // lazily by the view model (the track record itself carries no artwork),
    // then pushed to the already-rendered row through PropertyChanged.
    public string? ArtworkUrl
    {
        get => _artworkUrl;
        set
        {
            if (!string.Equals(_artworkUrl, value, StringComparison.Ordinal))
            {
                _artworkUrl = value;
                OnPropertyChanged();
            }
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        var handler = PropertyChanged;
        if (handler == null) return;

        var dispatcher = s_uiDispatcher;
        if (dispatcher != null)
        {
            dispatcher(() =>
            {
                try
                {
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName ?? ""));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[QueueItem] PropertyChanged UI dispatch failed: {ex.Message}");
                }
            });
            return;
        }

        // Fallback for test/non-UI environments: safely invoke handlers and protect against cross-thread COM exceptions
        var args = new System.ComponentModel.PropertyChangedEventArgs(propertyName ?? "");
        foreach (System.ComponentModel.PropertyChangedEventHandler d in handler.GetInvocationList())
        {
            try
            {
                d.Invoke(this, args);
            }
            catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x8001010E) || ex.HResult == unchecked((int)0x80004002) || ex.HResult == unchecked((int)0x8000FFFF))
            {
                System.Diagnostics.Debug.WriteLine($"[QueueItem] Caught cross-thread COMException (0x{ex.HResult:X8}) on '{propertyName}': {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QueueItem] PropertyChanged invocation threw: {ex.Message}");
            }
        }
    }
}

public enum PlaybackStatus { Stopped, Playing, Paused, Buffering }

public enum RepeatMode { None, Track, Queue }

public record PlaybackState(
    Track? CurrentTrack,
    PlaybackStatus Status,
    double PositionSeconds,
    double DurationSeconds,
    float Volume,
    bool IsMuted,
    bool IsShuffle,
    RepeatMode RepeatMode,
    long SequenceToken = 0
);

public enum EntityType { Album, Artist, Track, Genre, Playlist }
public record EntityNavigationParameter(EntityType Type, string Id);

// Snapshot of the player used to resume the queue after an app restart.
public record PersistedPlayerState(
    System.Collections.Generic.List<string> TrackIds,
    int CurrentIndex,
    double PositionSeconds,
    float Volume,
    bool IsShuffle,
    RepeatMode RepeatMode,
    System.Collections.Generic.List<string>? UnshuffledTrackIds = null
);

public record SearchResults(
    System.Collections.Generic.List<Track> Tracks,
    System.Collections.Generic.List<Album> Albums,
    System.Collections.Generic.List<Artist> Artists,
    System.Collections.Generic.List<Playlist>? Playlists = null
);
public record SearchSuggestion(string Title, EntityType Type, string Id);

// A cluster of tracks that appear to be the same composition across formats.
public record DuplicateGroup(string Title, string ArtistName, System.Collections.Generic.List<Track> Tracks);

public enum AudioDeviceCategory
{
    LaptopSpeakers,
    DesktopSpeakers,
    MonitorSpeakers,
    ExternalSpeakers,
    Headphones,
    Bluetooth,
    TypeC_USBAudio,
    AVReceiver_Amplifier,
    HDMI_DisplayAudio,
    Unknown
}

public record AudioOutputDeviceInfo(
    int Index,
    string Id,
    string Name,
    string Driver,
    string DeviceType,
    bool IsDefault,
    bool IsEnabled,
    AudioDeviceCategory Category = AudioDeviceCategory.Unknown,
    string Glyph = "\uE7F5"
);

public record AudioQualityDetails(
    string StreamQuality,
    string CodecFormat,
    int BitDepth,
    double SampleRateKhz,
    string ChannelsText,
    string DecoderEngine,
    string ResamplingStatus,
    string QualityBadgeType,
    string OutputDeviceName,
    string OutputDeviceQuality,
    string DspStatus,
    string OutputDeviceType = "Audio Endpoint",
    bool IsBitMatched = false,
    int? BitrateKbps = null,
    AudioDeviceCategory OutputDeviceCategory = AudioDeviceCategory.Unknown,
    string OutputDeviceGlyph = "\uE7F5"
);