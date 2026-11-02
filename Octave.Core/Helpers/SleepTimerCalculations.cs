using System;

namespace Octave.Core.Helpers;

/// <summary>
/// Result of circular countdown arc geometry calculations.
/// All coordinates and angles are pure mathematical values independent of UI frameworks.
/// </summary>
public readonly record struct ArcGeometryResult(
    bool IsFullCircle,
    bool IsEmpty,
    double StartX,
    double StartY,
    double EndX,
    double EndY,
    double RadiusX,
    double RadiusY,
    bool IsLargeArc,
    double ElapsedAngleRadians
);

/// <summary>
/// Result of extending an active sleep timer.
/// </summary>
public readonly record struct ExtendedTimerResult(
    int NewRemainingSeconds,
    double NewInitialSeconds,
    int TotalExtraMinutesAdded,
    double NewProgressFraction,
    string FormattedRemaining
);

/// <summary>
/// Pure algorithmic and mathematical helpers for sleep timer countdown,
/// clockwise arc geometry, duration presets, and extension handling.
/// </summary>
public static class SleepTimerCalculations
{
    /// <summary>
    /// Formats a sleep timer duration preset in human-readable form (e.g., "15 minutes", "1 hour", "1.5 hours").
    /// </summary>
    public static string FormatDurationPreset(int minutes)
    {
        return minutes switch
        {
            <= 0 => "0 minutes",
            60 => "1 hour",
            90 => "1.5 hours",
            120 => "2 hours",
            >= 60 when minutes % 60 == 0 => $"{minutes / 60} hours",
            >= 60 => $"{minutes / 60}h {minutes % 60}m",
            _ => $"{minutes} minutes"
        };
    }

    /// <summary>
    /// Formats countdown remaining time with adaptive precision:
    /// - Under 60s: "45s"
    /// - Under 60m: "14m 30s"
    /// - 60m and above: "1h 15m"
    /// </summary>
    public static string FormatRemainingTime(int seconds)
    {
        if (seconds <= 0) return "0s";

        int mins = seconds / 60;
        int secs = seconds % 60;

        if (mins >= 60)
        {
            return $"{mins / 60}h {(mins % 60):D2}m";
        }

        if (mins > 0)
        {
            return $"{mins}m {secs:D2}s";
        }

        return $"{secs}s";
    }

    /// <summary>
    /// Calculates progress fraction clamped between 0.0 and 1.0.
    /// Handles NaN, Infinity, negative, and zero values safely.
    /// </summary>
    public static double CalculateProgressFraction(double remainingSeconds, double initialSeconds)
    {
        if (double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds) ||
            double.IsNaN(initialSeconds) || double.IsInfinity(initialSeconds) ||
            initialSeconds <= 0.0 || remainingSeconds <= 0.0)
        {
            return 0.0;
        }

        return Math.Clamp(remainingSeconds / initialSeconds, 0.0, 1.0);
    }

    /// <summary>
    /// Computes the start point, end point, arc sweep direction, and large arc flag
    /// for a circular countdown boundary where time reduces clockwise.
    /// - At fraction = 1.0 (100%), IsFullCircle is true.
    /// - As time elapses (fraction reduces towards 0), the gap opens clockwise starting at 12 o'clock.
    ///   The remaining arc starts at elapsedAngle and sweeps clockwise to 12 o'clock.
    /// - At fraction &lt;= 0.001, IsEmpty is true.
    /// </summary>
    public static ArcGeometryResult CalculateArcGeometryPoints(double fraction, double size, double radius)
    {
        double cx = size / 2.0;
        double cy = size / 2.0;

        if (fraction >= 0.999)
        {
            return new ArcGeometryResult(
                IsFullCircle: true,
                IsEmpty: false,
                StartX: cx,
                StartY: cy - radius,
                EndX: cx,
                EndY: cy - radius,
                RadiusX: radius,
                RadiusY: radius,
                IsLargeArc: false,
                ElapsedAngleRadians: 0.0
            );
        }

        if (fraction <= 0.001)
        {
            return new ArcGeometryResult(
                IsFullCircle: false,
                IsEmpty: true,
                StartX: cx,
                StartY: cy - radius,
                EndX: cx,
                EndY: cy - radius,
                RadiusX: radius,
                RadiusY: radius,
                IsLargeArc: false,
                ElapsedAngleRadians: 2.0 * Math.PI
            );
        }

        // Clockwise reduction:
        // Elapsed angle advances clockwise from 12 o'clock (0 rad) by (1 - fraction) * 2pi.
        // The remaining active arc begins at elapsedAngle and sweeps clockwise to 12 o'clock.
        double elapsedAngle = (1.0 - fraction) * 2.0 * Math.PI;

        double startX = cx + radius * Math.Sin(elapsedAngle);
        double startY = cy - radius * Math.Cos(elapsedAngle);

        double endX = cx;
        double endY = cy - radius;

        return new ArcGeometryResult(
            IsFullCircle: false,
            IsEmpty: false,
            StartX: startX,
            StartY: startY,
            EndX: endX,
            EndY: endY,
            RadiusX: radius,
            RadiusY: radius,
            IsLargeArc: fraction > 0.5,
            ElapsedAngleRadians: elapsedAngle
        );
    }

    /// <summary>
    /// Calculates the updated state when extending an active sleep timer by extra minutes.
    /// </summary>
    public static ExtendedTimerResult CalculateExtendedTimer(int remainingSeconds, double initialSeconds, int extraMinutes, int currentExtraMinutesAdded)
    {
        if (extraMinutes <= 0)
        {
            double currentFrac = CalculateProgressFraction(remainingSeconds, initialSeconds);
            return new ExtendedTimerResult(
                remainingSeconds,
                initialSeconds,
                currentExtraMinutesAdded,
                currentFrac,
                FormatRemainingTime(remainingSeconds)
            );
        }

        int extraSeconds = extraMinutes * 60;
        int newRemaining = Math.Max(0, remainingSeconds + extraSeconds);
        double newInitial = Math.Max(1.0, initialSeconds + extraSeconds);
        int newTotalExtra = currentExtraMinutesAdded + extraMinutes;
        double newFraction = CalculateProgressFraction(newRemaining, newInitial);
        string formatted = FormatRemainingTime(newRemaining);

        return new ExtendedTimerResult(
            newRemaining,
            newInitial,
            newTotalExtra,
            newFraction,
            formatted
        );
    }

    /// <summary>
    /// Computes the total remaining playback seconds in the active queue.
    /// Sums the current track's remaining playback seconds and all subsequent queued tracks' durations.
    /// </summary>
    public static double CalculateQueueRemainingSeconds(double currentTrackRemaining, System.Collections.Generic.IEnumerable<double>? subsequentTrackDurations)
    {
        double total = double.IsNaN(currentTrackRemaining) || double.IsInfinity(currentTrackRemaining) || currentTrackRemaining < 0.0
            ? 0.0
            : currentTrackRemaining;

        if (subsequentTrackDurations != null)
        {
            foreach (double duration in subsequentTrackDurations)
            {
                if (!double.IsNaN(duration) && !double.IsInfinity(duration) && duration > 0.0)
                {
                    total += duration;
                }
            }
        }

        return total;
    }

    /// <summary>
    /// Formats a completion action for display in UI dropdowns.
    /// </summary>
    public static string FormatCompletionAction(Models.SleepTimerCompletionAction action)
    {
        return action switch
        {
            Models.SleepTimerCompletionAction.StopPlayback => "Stop playback",
            Models.SleepTimerCompletionAction.SleepPC => "Sleep PC",
            Models.SleepTimerCompletionAction.ShutdownPC => "Shut down PC",
            Models.SleepTimerCompletionAction.SignOut => "Sign out",
            _ => "Stop playback"
        };
    }

    /// <summary>
    /// Gets the user-facing explanation/warning text for a power-related completion action.
    /// Returns null if the action does not require a warning (e.g. StopPlayback).
    /// </summary>
    public static string? GetCompletionActionWarningText(Models.SleepTimerCompletionAction action)
    {
        return action switch
        {
            Models.SleepTimerCompletionAction.SleepPC =>
                "Selecting 'Sleep PC' will put your computer to sleep when the sleep timer expires.\n\nPlease save all unsaved work in other applications to prevent any data loss.\n\nDo you want to proceed with this action?",
            Models.SleepTimerCompletionAction.ShutdownPC =>
                "Selecting 'Shut down PC' will shut down your computer when the sleep timer expires.\n\nPlease save all unsaved work in other applications now.\n\nNote: If another application reports unsaved work or requires attention, Windows will cancel the shutdown to protect your files.\n\nDo you want to proceed with this action?",
            Models.SleepTimerCompletionAction.SignOut =>
                "Selecting 'Sign out' will sign you out of Windows when the sleep timer expires, closing all open applications.\n\nPlease save all unsaved work in other applications now.\n\nNote: If another application reports unsaved work, Windows will cancel the sign out to protect your files.\n\nDo you want to proceed with this action?",
            _ => null
        };
    }
}
