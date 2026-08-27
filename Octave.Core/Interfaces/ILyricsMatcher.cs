using System.Collections.Generic;
using Octave.Core.Models;

namespace Octave.Core.Interfaces;

public interface ILyricsMatcher
{
    string NormalizeTitle(string? title);
    IReadOnlyList<string> ParseArtistSet(string? artist);
    (LrclibResponse? BestCandidate, double Score) SelectBestCandidate(
        string targetTitle,
        string targetArtist,
        string? targetAlbum,
        double targetDurationSeconds,
        IReadOnlyList<LrclibResponse> candidates,
        double minScore = 0.60);
}
