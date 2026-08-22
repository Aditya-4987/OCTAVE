using System.Collections.Generic;

namespace Octave.Core.Models;

public record EnrichedArtistProfile(
    string ArtistId,
    string Name,
    string? Biography,
    string? LocalImageToken,
    string? PrimaryGenre,
    string? Country,
    int? FormedYear,
    int? DisbandedYear,
    IReadOnlyList<string>? Tags,
    IReadOnlyDictionary<string, string>? ExternalLinks,
    IReadOnlyList<string>? RelatedArtists,
    ExternalIds ExternalIds,
    string ProviderName
);

public class TheAudioDbOptions
{
    public string ApiKey { get; set; } = "2"; // Default open test key for TheAudioDB
    public string BaseUrl { get; set; } = "https://www.theaudiodb.com/api/v1/json";
}
