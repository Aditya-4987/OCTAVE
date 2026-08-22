using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Octave.Core.Services.External.MusicBrainz;

// =================================================================
// RECORDING DTOs
// =================================================================

public class MbRecordingSearchResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("recordings")]
    public List<MbRecordingDto>? Recordings { get; set; }
}

public class MbRecordingDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public int? Score { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("length")]
    public int? LengthMs { get; set; }

    [JsonPropertyName("disambiguation")]
    public string? Disambiguation { get; set; }

    [JsonPropertyName("first-release-date")]
    public string? FirstReleaseDate { get; set; }

    [JsonPropertyName("isrcs")]
    public List<string>? Isrcs { get; set; }

    [JsonPropertyName("artist-credit")]
    public List<MbArtistCreditDto>? ArtistCredit { get; set; }

    [JsonPropertyName("releases")]
    public List<MbReleaseDto>? Releases { get; set; }

    [JsonPropertyName("tags")]
    public List<MbTagDto>? Tags { get; set; }

    [JsonPropertyName("genres")]
    public List<MbTagDto>? Genres { get; set; }
}

// =================================================================
// ARTIST DTOs
// =================================================================

public class MbArtistSearchResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("artists")]
    public List<MbArtistDto>? Artists { get; set; }
}

public class MbArtistDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sort-name")]
    public string? SortName { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("disambiguation")]
    public string? Disambiguation { get; set; }

    [JsonPropertyName("score")]
    public int? Score { get; set; }

    [JsonPropertyName("life-span")]
    public MbLifeSpanDto? LifeSpan { get; set; }

    [JsonPropertyName("tags")]
    public List<MbTagDto>? Tags { get; set; }

    [JsonPropertyName("genres")]
    public List<MbTagDto>? Genres { get; set; }
}

public class MbArtistCreditDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("joinphrase")]
    public string? JoinPhrase { get; set; }

    [JsonPropertyName("artist")]
    public MbArtistDto? Artist { get; set; }
}

public class MbLifeSpanDto
{
    [JsonPropertyName("begin")]
    public string? Begin { get; set; }

    [JsonPropertyName("end")]
    public string? End { get; set; }

    [JsonPropertyName("ended")]
    public bool? Ended { get; set; }
}

// =================================================================
// RELEASE & RELEASE GROUP DTOs
// =================================================================

public class MbReleaseSearchResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("releases")]
    public List<MbReleaseDto>? Releases { get; set; }
}

public class MbReleaseDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("track-count")]
    public int? TrackCount { get; set; }

    [JsonPropertyName("score")]
    public int? Score { get; set; }

    [JsonPropertyName("disambiguation")]
    public string? Disambiguation { get; set; }

    [JsonPropertyName("artist-credit")]
    public List<MbArtistCreditDto>? ArtistCredit { get; set; }

    [JsonPropertyName("release-group")]
    public MbReleaseGroupDto? ReleaseGroup { get; set; }

    [JsonPropertyName("media")]
    public List<MbMediumDto>? Media { get; set; }

    [JsonPropertyName("tags")]
    public List<MbTagDto>? Tags { get; set; }

    [JsonPropertyName("genres")]
    public List<MbTagDto>? Genres { get; set; }
}

public class MbReleaseGroupDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("primary-type")]
    public string? PrimaryType { get; set; }

    [JsonPropertyName("first-release-date")]
    public string? FirstReleaseDate { get; set; }

    [JsonPropertyName("artist-credit")]
    public List<MbArtistCreditDto>? ArtistCredit { get; set; }

    [JsonPropertyName("tags")]
    public List<MbTagDto>? Tags { get; set; }

    [JsonPropertyName("genres")]
    public List<MbTagDto>? Genres { get; set; }
}

public class MbMediumDto
{
    [JsonPropertyName("position")]
    public int? Position { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("track-count")]
    public int? TrackCount { get; set; }

    [JsonPropertyName("tracks")]
    public List<MbTrackDto>? Tracks { get; set; }
}

public class MbTrackDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("number")]
    public string? Number { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("length")]
    public int? LengthMs { get; set; }

    [JsonPropertyName("recording")]
    public MbRecordingDto? Recording { get; set; }
}

public class MbTagDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int? Count { get; set; }
}
