using Windows.Media;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using System;

namespace Octave_Desktop.Services.System;

public class WindowsSmtcService : ISmtcService
{
    private readonly IQueueService _queueService;
    private SystemMediaTransportControls? _systemControls;

    public WindowsSmtcService(IQueueService queueService)
    {
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
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

        // Update display details
        var updater = _systemControls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = state.CurrentTrack?.Title ?? "No Track Loaded";
        updater.MusicProperties.Artist = state.CurrentTrack?.ArtistName ?? "Octave Engine";
        updater.Update();
    }
}
