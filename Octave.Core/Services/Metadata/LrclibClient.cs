using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Interfaces;
using Octave.Core.Models;

namespace Octave.Core.Services.Metadata;

public class LrclibClient : ILrclibClient
{
    private readonly HttpClient _httpClient;
    public const string DefaultBaseUrl = "https://lrclib.net";
    public const string UserAgentValue = "OCTAVE v1.0.0 (https://github.com/Aditya-4987/OCTAVE)";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LrclibClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<LrclibResponse?> GetLyricsAsync(
        string trackTitle,
        string artistName,
        string? albumName = null,
        double? durationSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackTitle) || string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var queryParams = new List<string>
        {
            $"track_name={Uri.EscapeDataString(trackTitle.Trim())}",
            $"artist_name={Uri.EscapeDataString(artistName.Trim())}"
        };

        if (!string.IsNullOrWhiteSpace(albumName))
        {
            queryParams.Add($"album_name={Uri.EscapeDataString(albumName.Trim())}");
        }

        if (durationSeconds.HasValue && durationSeconds.Value >= 1 && durationSeconds.Value <= 3600)
        {
            int roundedDuration = (int)Math.Round(durationSeconds.Value);
            queryParams.Add($"duration={roundedDuration}");
        }

        string endpoint = $"/api/get?{string.Join("&", queryParams)}";
        Uri requestUri = _httpClient.BaseAddress != null
            ? new Uri(_httpClient.BaseAddress, endpoint)
            : new Uri(DefaultBaseUrl + endpoint);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgentValue);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<LrclibResponse>(stream, JsonOptions, cancellationToken);
    }
}
