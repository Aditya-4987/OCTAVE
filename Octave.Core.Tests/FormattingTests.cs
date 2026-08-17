using System;
using Xunit;

namespace Octave.Core.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(-5, "0:00")]
    [InlineData(45, "0:45")]
    [InlineData(65, "1:05")]
    [InlineData(599, "9:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3665, "1:01:05")]
    [InlineData(7325, "2:02:05")]
    public void FormatSeconds_MatchesExpectedString(double seconds, string expected)
    {
        var result = FormatSeconds(seconds);
        Assert.Equal(expected, result);
    }

    private static string FormatSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0) return "0:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }
}
