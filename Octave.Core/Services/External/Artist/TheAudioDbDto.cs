using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Octave.Core.Services.External.Artist;

public class TadbArtistResponse
{
    [JsonPropertyName("artists")]
    public List<TadbArtistDto>? Artists { get; set; }
}

public class TadbArtistDto
{
    [JsonPropertyName("idArtist")]
    public string? IdArtist { get; set; }

    [JsonPropertyName("strArtist")]
    public string? StrArtist { get; set; }

    [JsonPropertyName("strBiographyEN")]
    public string? StrBiographyEN { get; set; }

    [JsonPropertyName("strArtistThumb")]
    public string? StrArtistThumb { get; set; }

    [JsonPropertyName("strArtistFanart")]
    public string? StrArtistFanart { get; set; }

    [JsonPropertyName("strArtistLogo")]
    public string? StrArtistLogo { get; set; }

    [JsonPropertyName("strGenre")]
    public string? StrGenre { get; set; }

    [JsonPropertyName("strCountry")]
    public string? StrCountry { get; set; }

    [JsonPropertyName("intBornYear")]
    public string? IntBornYear { get; set; }

    [JsonPropertyName("intDiedYear")]
    public string? IntDiedYear { get; set; }

    [JsonPropertyName("strWebsite")]
    public string? StrWebsite { get; set; }

    [JsonPropertyName("strTwitter")]
    public string? StrTwitter { get; set; }

    [JsonPropertyName("strFacebook")]
    public string? StrFacebook { get; set; }

    [JsonPropertyName("strMusicBrainzID")]
    public string? StrMusicBrainzID { get; set; }
}
