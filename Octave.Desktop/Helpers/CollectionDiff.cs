using System;
using System.Collections.Generic;

namespace Octave_Desktop.Helpers;

/// <summary>
/// VM-10: identity-based diff helpers so observable collection rebuilds can be
/// skipped when the incoming data already matches what is displayed - avoids
/// full re-render flicker and selection loss on every redundant
/// LibraryUpdated wave. The Albums/Artists/Playlists/Library view models carry
/// inline SequenceEqual guards; this centralizes the same check for the VMs
/// that were missing it.
/// </summary>
internal static class CollectionDiff
{
    /// <summary>
    /// True when both lists have the same length and pairwise-equal ids
    /// (ordinal). Order-sensitive by design: a genuine reorder must rebuild.
    /// </summary>
    public static bool SameIdSequence<T>(IReadOnlyList<T> current, IReadOnlyList<T> incoming, Func<T, string?> idSelector)
    {
        if (current.Count != incoming.Count) return false;

        for (int i = 0; i < current.Count; i++)
        {
            if (!string.Equals(idSelector(current[i]), idSelector(incoming[i]), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
