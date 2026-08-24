using System;
using Octave.Core.Helpers;
using Xunit;

namespace Octave.Core.Tests;

public class FormattingTests
{
    // TEST-03: this theory drove a private copy of the formatting logic, so the
    // production path (DurationFormatter, shared by the Desktop converter and the
    // mini player) was untested. Call the real implementation.
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(-5, "0:00")]
    [InlineData(double.NaN, "0:00")]
    [InlineData(double.PositiveInfinity, "0:00")]
    [InlineData(double.NegativeInfinity, "0:00")]
    [InlineData(45, "0:45")]
    [InlineData(59.9, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(65, "1:05")]
    [InlineData(599, "9:59")]
    [InlineData(600, "10:00")]
    [InlineData(3599.99, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3665, "1:01:05")]
    [InlineData(7325, "2:02:05")]
    public void FormatSeconds_MatchesExpectedString(double seconds, string expected)
    {
        Assert.Equal(expected, DurationFormatter.FormatSeconds(seconds));
    }
}
