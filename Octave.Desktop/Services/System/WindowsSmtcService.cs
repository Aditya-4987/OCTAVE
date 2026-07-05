using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Library;
using System;
using System.Threading.Tasks;

namespace Octave_Desktop.Services.System;

public class WindowsSmtcService : ISmtcService
{
    private readonly IQueueService _queueService;
    private readonly ILibraryService _libraryService;
    private readonly IArtworkCacheManager _artworkCache;
    private SystemMediaTransportControls? _systemControls;
    private string? _lastTrackId;
    private bool _hasPushedDisplay;

    public WindowsSmtcService(IQueueService queueService, ILibraryService libraryService, IArtworkCacheManager artworkCache)
    {
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _artworkCache = artworkCache ?? throw new ArgumentNullException(nameof(artworkCache));
    }

    public void Initialize(IntPtr windowHandle)
    {
        if (_systemControls != null) return;

        // Retrieve the SMTC instance bound to the window handle
        _systemControls = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);

        // Configure system capabilities
        _systemControls.IsPlayEnabled = true;
        _systemControls.IsPauseEnabled = true;
        _systemControls.IsNextEnabled = true;
        _systemControls.IsPreviousEnabled = true;

        // Handle inbound hardware button keys
        _systemControls.ButtonPressed += SystemControls_ButtonPressed;

        // Monitor outbound queue playback states
        _queueService.PlaybackStateChanged += QueueService_PlaybackStateChanged;

        // Initial sync of metadata if track is already active
        UpdateSmtcState(_queueService.CurrentState);
    }

    private void SystemControls_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        // OS command events execute on worker background threads.
        // QueueService operations handle their own internal locks for thread safety.
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                _queueService.Resume();
                break;
            case SystemMediaTransportControlsButton.Pause:
                _queueService.Pause();
                break;
            case SystemMediaTransportControlsButton.Next:
                _queueService.PlayNext();
                break;
            case SystemMediaTransportControlsButton.Previous:
                _queueService.PlayPrevious();
                break;
        }
    }

    private void QueueService_PlaybackStateChanged(object? sender, PlaybackState state)
    {
        UpdateSmtcState(state);
    }

    private void UpdateSmtcState(PlaybackState state)
    {
        if (_systemControls == null) return;

        // Update playback transport status
        _systemControls.PlaybackStatus = state.Status switch
        {
            PlaybackStatus.Playing => MediaPlaybackStatus.Playing,
            PlaybackStatus.Paused => MediaPlaybackStatus.Paused,
            PlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
            PlaybackStatus.Buffering => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Closed
        };

        // Only push display metadata when the track actually changes. The OS
        // shell doesn't need Title/Artist re-sent on every play/pause/volume
        // tweak, and DisplayUpdater.Update() is a comparatively heavy call.
        var trackId = state.CurrentTrack?.Id;
        if (trackId != _lastTrackId || !_hasPushedDisplay)
        {
            _lastTrackId = trackId;
            _hasPushedDisplay = true;

            var updater = _systemControls.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = state.CurrentTrack?.Title ?? "No Track Loaded";
            updater.MusicProperties.Artist = state.CurrentTrack?.ArtistName ?? "Octave Engine";
            updater.Thumbnail = null; // cleared here; re-populated async below
            updater.Update();

            // Resolve and push album art asynchronously (a second, lighter update).
            if (state.CurrentTrack != null)
            {
                _ = UpdateThumbnailAsync(state.CurrentTrack);
            }
        }
    }

    private async Task UpdateThumbnailAsync(Track track)
    {
        if (_systemControls == null) return;
        string capturedTrackId = track.Id;

        try
        {
            var album = await _libraryService.GetAlbumByIdAsync(track.AlbumId);
            string? token = album?.ArtworkUrl;

            // Bail if the track changed again while we were loading.
            if (capturedTrackId != _lastTrackId || string.IsNullOrWhiteSpace(token)) return;

            string fileName = token.Contains('/') ? token.Substring(token.LastIndexOf('/') + 1) : token;
            string absolute = global::System.IO.Path.Combine(_artworkCache.CacheRoot, fileName);
            if (!global::System.IO.File.Exists(absolute)) return;

            var file = await StorageFile.GetFileFromPathAsync(absolute);
            if (capturedTrackId != _lastTrackId) return;

            var updater = _systemControls.DisplayUpdater;
            updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
            updater.Update();
        }
        catch (Exception ex)
        {
            global::System.Diagnostics.Debug.WriteLine($"[SMTC] Thumbnail update failed: {ex.Message}");
        }
    }
}
