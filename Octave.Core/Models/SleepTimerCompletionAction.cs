namespace Octave.Core.Models;

/// <summary>
/// Actions to perform when the sleep timer countdown expires.
/// </summary>
public enum SleepTimerCompletionAction
{
    /// <summary>
    /// Smoothly fades out audio and pauses playback (default).
    /// </summary>
    StopPlayback = 0,

    /// <summary>
    /// Smoothly fades out audio, pauses playback, and puts the computer to sleep.
    /// </summary>
    SleepPC = 1,

    /// <summary>
    /// Smoothly fades out audio, pauses playback, and initiates a safe, non-forced PC shutdown.
    /// If an application requires attention to save work, the shutdown is safely aborted.
    /// </summary>
    ShutdownPC = 2,

    /// <summary>
    /// Smoothly fades out audio, pauses playback, and initiates a safe, non-forced Windows sign-out / logoff.
    /// </summary>
    SignOut = 3
}
