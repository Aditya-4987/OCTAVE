using System;
using Octave.Core.Helpers;
using Xunit;

namespace Octave.Core.Tests;

public class SleepTimerTests
{
    [Theory]
    [InlineData(0, "0 minutes")]
    [InlineData(-5, "0 minutes")]
    [InlineData(5, "5 minutes")]
    [InlineData(10, "10 minutes")]
    [InlineData(15, "15 minutes")]
    [InlineData(20, "20 minutes")]
    [InlineData(30, "30 minutes")]
    [InlineData(45, "45 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(90, "1.5 hours")]
    [InlineData(120, "2 hours")]
    [InlineData(180, "3 hours")]
    [InlineData(75, "1h 15m")]
    [InlineData(150, "2h 30m")]
    public void FormatDurationPreset_FormatsCorrectly(int minutes, string expected)
    {
        string actual = SleepTimerCalculations.FormatDurationPreset(minutes);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(-10, "0s")]
    [InlineData(1, "1s")]
    [InlineData(45, "45s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m 00s")]
    [InlineData(61, "1m 01s")]
    [InlineData(125, "2m 05s")]
    [InlineData(870, "14m 30s")]
    [InlineData(3599, "59m 59s")]
    [InlineData(3600, "1h 00m")]
    [InlineData(3660, "1h 01m")]
    [InlineData(4500, "1h 15m")]
    [InlineData(7200, "2h 00m")]
    [InlineData(7380, "2h 03m")]
    public void FormatRemainingTime_AdaptivePrecision_FormatsCorrectly(int seconds, string expected)
    {
        string actual = SleepTimerCalculations.FormatRemainingTime(seconds);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(50.0, 100.0, 0.5)]
    [InlineData(100.0, 100.0, 1.0)]
    [InlineData(0.0, 100.0, 0.0)]
    [InlineData(-10.0, 100.0, 0.0)]
    [InlineData(150.0, 100.0, 1.0)]
    [InlineData(50.0, 0.0, 0.0)]
    [InlineData(50.0, -100.0, 0.0)]
    public void CalculateProgressFraction_ValidBoundsAndClamping(double remaining, double initial, double expected)
    {
        double actual = SleepTimerCalculations.CalculateProgressFraction(remaining, initial);
        Assert.Equal(expected, actual, precision: 4);
    }

    [Fact]
    public void CalculateProgressFraction_DefensiveAgainstNaNAndInfinity()
    {
        Assert.Equal(0.0, SleepTimerCalculations.CalculateProgressFraction(double.NaN, 100.0));
        Assert.Equal(0.0, SleepTimerCalculations.CalculateProgressFraction(50.0, double.NaN));
        Assert.Equal(0.0, SleepTimerCalculations.CalculateProgressFraction(double.PositiveInfinity, 100.0));
        Assert.Equal(0.0, SleepTimerCalculations.CalculateProgressFraction(50.0, double.PositiveInfinity));
        Assert.Equal(0.0, SleepTimerCalculations.CalculateProgressFraction(double.NegativeInfinity, 100.0));
    }

    [Fact]
    public void CalculateArcGeometryPoints_FullCircle_ReturnsFullCircleResult()
    {
        double size = 34.0;
        double radius = 15.0;

        var result = SleepTimerCalculations.CalculateArcGeometryPoints(1.0, size, radius);
        Assert.True(result.IsFullCircle);
        Assert.False(result.IsEmpty);
        Assert.Equal(radius, result.RadiusX);
        Assert.Equal(radius, result.RadiusY);
        Assert.Equal(0.0, result.ElapsedAngleRadians);

        var nearFull = SleepTimerCalculations.CalculateArcGeometryPoints(0.9995, size, radius);
        Assert.True(nearFull.IsFullCircle);
        Assert.False(nearFull.IsEmpty);
    }

    [Fact]
    public void CalculateArcGeometryPoints_EmptyCircle_ReturnsEmptyResult()
    {
        double size = 34.0;
        double radius = 15.0;

        var result = SleepTimerCalculations.CalculateArcGeometryPoints(0.0, size, radius);
        Assert.False(result.IsFullCircle);
        Assert.True(result.IsEmpty);

        var nearEmpty = SleepTimerCalculations.CalculateArcGeometryPoints(0.0005, size, radius);
        Assert.True(nearEmpty.IsEmpty);
    }

    [Fact]
    public void CalculateArcGeometryPoints_QuarterElapsed_StartsAtThreeOClock()
    {
        // fraction = 0.75 (25% elapsed, 75% remaining)
        // Elapsed angle = 90 deg (pi / 2 rad) -> gap at 12 to 3 o'clock.
        // Arc starts at 3 o'clock (startX = cx + radius, startY = cy) and sweeps clockwise to 12 o'clock.
        double size = 34.0;
        double radius = 15.0;
        double cx = 17.0;
        double cy = 17.0;

        var result = SleepTimerCalculations.CalculateArcGeometryPoints(0.75, size, radius);
        Assert.False(result.IsFullCircle);
        Assert.False(result.IsEmpty);
        Assert.True(result.IsLargeArc); // 0.75 > 0.5

        Assert.Equal(cx + radius, result.StartX, precision: 3);
        Assert.Equal(cy, result.StartY, precision: 3);

        // End point is always 12 o'clock
        Assert.Equal(cx, result.EndX, precision: 3);
        Assert.Equal(cy - radius, result.EndY, precision: 3);
        Assert.Equal(Math.PI / 2.0, result.ElapsedAngleRadians, precision: 3);
    }

    [Fact]
    public void CalculateArcGeometryPoints_HalfElapsed_StartsAtSixOClock()
    {
        // fraction = 0.50 (50% elapsed, 50% remaining)
        // Elapsed angle = 180 deg (pi rad) -> gap from 12 to 6 o'clock.
        // Arc starts at 6 o'clock (startX = cx, startY = cy + radius) and sweeps clockwise to 12 o'clock.
        double size = 34.0;
        double radius = 15.0;
        double cx = 17.0;
        double cy = 17.0;

        var result = SleepTimerCalculations.CalculateArcGeometryPoints(0.50, size, radius);
        Assert.False(result.IsFullCircle);
        Assert.False(result.IsEmpty);
        Assert.False(result.IsLargeArc); // 0.50 <= 0.5

        Assert.Equal(cx, result.StartX, precision: 3);
        Assert.Equal(cy + radius, result.StartY, precision: 3);

        Assert.Equal(cx, result.EndX, precision: 3);
        Assert.Equal(cy - radius, result.EndY, precision: 3);
        Assert.Equal(Math.PI, result.ElapsedAngleRadians, precision: 3);
    }

    [Fact]
    public void CalculateArcGeometryPoints_ThreeQuartersElapsed_StartsAtNineOClock()
    {
        // fraction = 0.25 (75% elapsed, 25% remaining)
        // Elapsed angle = 270 deg (1.5 pi rad) -> gap from 12 to 9 o'clock.
        // Arc starts at 9 o'clock (startX = cx - radius, startY = cy) and sweeps clockwise to 12 o'clock.
        double size = 34.0;
        double radius = 15.0;
        double cx = 17.0;
        double cy = 17.0;

        var result = SleepTimerCalculations.CalculateArcGeometryPoints(0.25, size, radius);
        Assert.False(result.IsFullCircle);
        Assert.False(result.IsEmpty);
        Assert.False(result.IsLargeArc); // 0.25 <= 0.5

        Assert.Equal(cx - radius, result.StartX, precision: 3);
        Assert.Equal(cy, result.StartY, precision: 3);

        Assert.Equal(cx, result.EndX, precision: 3);
        Assert.Equal(cy - radius, result.EndY, precision: 3);
        Assert.Equal(1.5 * Math.PI, result.ElapsedAngleRadians, precision: 3);
    }

    [Fact]
    public void CalculateExtendedTimer_AddsExtraMinutes_CorrectlyUpdatesState()
    {
        // 5m remaining of initial 10m timer
        int remaining = 300;
        double initial = 600.0;

        // Add 5 minutes
        var ext1 = SleepTimerCalculations.CalculateExtendedTimer(remaining, initial, extraMinutes: 5, currentExtraMinutesAdded: 0);

        Assert.Equal(600, ext1.NewRemainingSeconds); // 10 minutes
        Assert.Equal(900.0, ext1.NewInitialSeconds); // 15 minutes
        Assert.Equal(5, ext1.TotalExtraMinutesAdded);
        Assert.Equal(600.0 / 900.0, ext1.NewProgressFraction, precision: 4);
        Assert.Equal("10m 00s", ext1.FormattedRemaining);

        // Add another 10 minutes cumulatively
        var ext2 = SleepTimerCalculations.CalculateExtendedTimer(ext1.NewRemainingSeconds, ext1.NewInitialSeconds, extraMinutes: 10, currentExtraMinutesAdded: ext1.TotalExtraMinutesAdded);

        Assert.Equal(1200, ext2.NewRemainingSeconds); // 20 minutes
        Assert.Equal(1500.0, ext2.NewInitialSeconds); // 25 minutes
        Assert.Equal(15, ext2.TotalExtraMinutesAdded);
        Assert.Equal(1200.0 / 1500.0, ext2.NewProgressFraction, precision: 4);
        Assert.Equal("20m 00s", ext2.FormattedRemaining);
    }

    [Fact]
    public void CalculateExtendedTimer_NonPositiveExtension_PreservesState()
    {
        int remaining = 180;
        double initial = 360.0;

        var extZero = SleepTimerCalculations.CalculateExtendedTimer(remaining, initial, extraMinutes: 0, currentExtraMinutesAdded: 5);
        Assert.Equal(remaining, extZero.NewRemainingSeconds);
        Assert.Equal(initial, extZero.NewInitialSeconds);
        Assert.Equal(5, extZero.TotalExtraMinutesAdded);
        Assert.Equal(0.5, extZero.NewProgressFraction, precision: 4);

        var extNegative = SleepTimerCalculations.CalculateExtendedTimer(remaining, initial, extraMinutes: -5, currentExtraMinutesAdded: 5);
        Assert.Equal(remaining, extNegative.NewRemainingSeconds);
        Assert.Equal(initial, extNegative.NewInitialSeconds);
        Assert.Equal(5, extNegative.TotalExtraMinutesAdded);
    }

    [Fact]
    public void CalculateQueueRemainingSeconds_CalculatesCorrectSum()
    {
        // Current track remaining = 150s; subsequent queued tracks: 180s and 240s -> total = 570s
        double currentTrackRemaining = 150.0;
        double[] subsequentDurations = [180.0, 240.0];

        double total = SleepTimerCalculations.CalculateQueueRemainingSeconds(currentTrackRemaining, subsequentDurations);

        Assert.Equal(570.0, total);
    }

    [Fact]
    public void CalculateQueueRemainingSeconds_NegativeOrNanCurrentRemaining_ClampsToZero()
    {
        double[] subsequentDurations = [100.0, 50.0];

        Assert.Equal(150.0, SleepTimerCalculations.CalculateQueueRemainingSeconds(-10.0, subsequentDurations));
        Assert.Equal(150.0, SleepTimerCalculations.CalculateQueueRemainingSeconds(double.NaN, subsequentDurations));
        Assert.Equal(150.0, SleepTimerCalculations.CalculateQueueRemainingSeconds(double.NegativeInfinity, subsequentDurations));
    }

    [Fact]
    public void CalculateQueueRemainingSeconds_NullOrEmptySubsequent_ReturnsCurrentTrackRemaining()
    {
        Assert.Equal(45.0, SleepTimerCalculations.CalculateQueueRemainingSeconds(45.0, null));
        Assert.Equal(45.0, SleepTimerCalculations.CalculateQueueRemainingSeconds(45.0, Array.Empty<double>()));
    }

    [Fact]
    public void CalculateQueueRemainingSeconds_FiltersInvalidSubsequentDurations()
    {
        double currentTrackRemaining = 60.0;
        double[] subsequentDurations = [120.0, -10.0, double.NaN, 0.0, double.PositiveInfinity, 180.0];

        double total = SleepTimerCalculations.CalculateQueueRemainingSeconds(currentTrackRemaining, subsequentDurations);

        Assert.Equal(360.0, total); // 60 + 120 + 180
    }

    [Theory]
    [InlineData(Octave.Core.Models.SleepTimerCompletionAction.StopPlayback, "Stop playback")]
    [InlineData(Octave.Core.Models.SleepTimerCompletionAction.SleepPC, "Sleep PC")]
    [InlineData(Octave.Core.Models.SleepTimerCompletionAction.ShutdownPC, "Shut down PC")]
    [InlineData(Octave.Core.Models.SleepTimerCompletionAction.SignOut, "Sign out")]
    public void FormatCompletionAction_ReturnsExpectedLabel(Octave.Core.Models.SleepTimerCompletionAction action, string expectedLabel)
    {
        Assert.Equal(expectedLabel, SleepTimerCalculations.FormatCompletionAction(action));
    }

    [Fact]
    public void GetCompletionActionWarningText_ReturnsNullForStopPlayback()
    {
        Assert.Null(SleepTimerCalculations.GetCompletionActionWarningText(Octave.Core.Models.SleepTimerCompletionAction.StopPlayback));
    }

    [Theory]
    [InlineData(Octave.Core.Models.SleepTimerCompletionAction.SleepPC)]
    [InlineData(Octave.Core.Models.SleepTimerCompletionAction.ShutdownPC)]
    [InlineData(Octave.Core.Models.SleepTimerCompletionAction.SignOut)]
    public void GetCompletionActionWarningText_ReturnsExplicitWarningForPowerActions(Octave.Core.Models.SleepTimerCompletionAction action)
    {
        string? warning = SleepTimerCalculations.GetCompletionActionWarningText(action);
        Assert.NotNull(warning);
        Assert.Contains("unsaved", warning, StringComparison.OrdinalIgnoreCase);
    }
}
