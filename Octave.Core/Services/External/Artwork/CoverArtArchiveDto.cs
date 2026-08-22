using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Octave.Core.Services.External.Artwork;

public class CaaReleaseResponse
{
    [JsonPropertyName("images")]
    public List<CaaImageDto>? Images { get; set; }

    [JsonPropertyName("release")]
    public string? Release { get; set; }
}

public class CaaImageDto
{
    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("approved")]
    public bool? Approved { get; set; }

    [JsonPropertyName("back")]
    public bool? Back { get; set; }

    [JsonPropertyName("front")]
    public bool Front { get; set; }

    [JsonPropertyName("image")]
    public string Image { get; set; } = string.Empty;

    [JsonPropertyName("thumbnails")]
    public CaaThumbnailsDto? Thumbnails { get; set; }
}

public class CaaThumbnailsDto
{
    [JsonPropertyName("250")]
    public string? Thumb250 { get; set; }

    [JsonPropertyName("500")]
    public string? Thumb500 { get; set; }

    [JsonPropertyName("1200")]
    public string? Thumb1200 { get; set; }

    [JsonPropertyName("small")]
    public string? Small { get; set; }

    [JsonPropertyName("large")]
    public string? Large { get; set; }
}
