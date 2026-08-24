using System;

namespace Octave.Core.Helpers;

// TEST-03: the canonical seconds→"m:ss"/"h:mm:ss" formatter. It used to exist
// only as a private copy inside the xUnit test plus diverging duplicates in the
// Desktop layer, so a regression in the real formatting could never fail a test.
// Every consumer formats through this one implementation now.
public static class DurationFormatter
{
    public static string FormatSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) return "0:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }
}
