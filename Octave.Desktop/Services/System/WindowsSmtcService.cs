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
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

    // SYS-03: written only on the dispatcher (see QueueService_PlaybackStateChanged);
    // volatile keeps the background-thread reads in UpdateThumbnailAsync honest.
    private volatile string? _lastTrackId;
    private volatile bool _hasPushedDisplay;
    private bool _disposed;

    public WindowsSmtcService(IQueueService queueService, ILibraryService libraryService, IArtworkCacheManager artworkCache)
    {
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _artworkCache = artworkCache ?? throw new ArgumentNullException(nameof(artworkCache));
    }

    public void Initialize(IntPtr windowHandle)
    {
        if (_systemControls != null) return;

        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

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
        var button = args.Button;

        void ProcessButton()
        {
            try
            {
                switch (button)
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
            catch (Exception ex)
            {
                global::System.Diagnostics.Debug.WriteLine($"[SMTC] Button processing failed ({button}): {ex.Message}");
            }
        }

        // SYS-04: the branch was inverted - ButtonPressed arrives on the UI
        // thread, so real usage always took the ThreadPool path and ran queue
        // mutations off the thread every binding reads. Dispatch
        // unconditionally; only a missing dispatcher (pre-initialize) falls
        // back to the pool.
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(ProcessButton);
        }
        else
        {
            global::System.Threading.ThreadPool.QueueUserWorkItem(_ => ProcessButton());
        }
    }

    private void QueueService_PlaybackStateChanged(object? sender, PlaybackState state)
    {
        // SYS-03: PlaybackStateChanged fires on the queue engine's background
        // thread while UpdateThumbnailAsync resumes on arbitrary threads - the
        // shared _lastTrackId/_hasPushedDisplay fields raced. Serializing all
        // state pushes through the dispatcher removes the race entirely.
        var dq = _dispatcherQueue;
        if (dq != null && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(() => UpdateSmtcState(state));
        }
        else
        {
            UpdateSmtcState(state);
        }
    }

    private void UpdateSmtcState(PlaybackState state)
    {
        if (_systemControls == null) return;

        try
        {
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
        catch (Exception ex)
        {
            global::System.Diagnostics.Debug.WriteLine($"[SMTC] UpdateSmtcState failed: {ex.Message}");
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
            if (_disposed || _systemControls == null) return;
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

    /// <summary>
    /// SYS-05: tears down both subscriptions and clears the OS-facing display
    /// metadata. Previously the singleton listened forever and the shell kept
    /// showing OCTAVE's last track (and reacting to buttons) after the window
    /// that owned the SMTC handle was gone.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queueService.PlaybackStateChanged -= QueueService_PlaybackStateChanged;

        try
        {
            if (_systemControls != null)
            {
                _systemControls.ButtonPressed -= SystemControls_ButtonPressed;
                _systemControls.DisplayUpdater.ClearAll();
                _systemControls.PlaybackStatus = MediaPlaybackStatus.Closed;
                _systemControls.DisplayUpdater.Update();
            }
        }
        catch (Exception ex)
        {
            global::System.Diagnostics.Debug.WriteLine($"[SMTC] Dispose failed: {ex.Message}");
        }

        _systemControls = null;
        _dispatcherQueue = null;
    }
}
