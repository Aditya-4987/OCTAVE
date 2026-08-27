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

    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(6);

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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultRequestTimeout);
        var token = cts.Token;

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

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonSerializer.DeserializeAsync<LrclibResponse>(stream, JsonOptions, token);
    }

    public async Task<LrclibResponse?> GetLyricsByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultRequestTimeout);
        var token = cts.Token;

        string endpoint = $"/api/get/{id}";
        Uri requestUri = _httpClient.BaseAddress != null
            ? new Uri(_httpClient.BaseAddress, endpoint)
            : new Uri(DefaultBaseUrl + endpoint);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgentValue);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonSerializer.DeserializeAsync<LrclibResponse>(stream, JsonOptions, token);
    }

    public async Task<IReadOnlyList<LrclibResponse>> SearchLyricsAsync(
        string? query = null,
        string? trackName = null,
        string? artistName = null,
        string? albumName = null,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultRequestTimeout);
        var token = cts.Token;

        var queryParams = new List<string>();

        if (!string.IsNullOrWhiteSpace(query))
        {
            queryParams.Add($"q={Uri.EscapeDataString(query.Trim())}");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(trackName))
            {
                queryParams.Add($"track_name={Uri.EscapeDataString(trackName.Trim())}");
            }
            if (!string.IsNullOrWhiteSpace(artistName))
            {
                queryParams.Add($"artist_name={Uri.EscapeDataString(artistName.Trim())}");
            }
            if (!string.IsNullOrWhiteSpace(albumName))
            {
                queryParams.Add($"album_name={Uri.EscapeDataString(albumName.Trim())}");
            }
        }

        if (queryParams.Count == 0)
        {
            return Array.Empty<LrclibResponse>();
        }

        string endpoint = $"/api/search?{string.Join("&", queryParams)}";
        Uri requestUri = _httpClient.BaseAddress != null
            ? new Uri(_httpClient.BaseAddress, endpoint)
            : new Uri(DefaultBaseUrl + endpoint);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgentValue);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<LrclibResponse>();
        }

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(token);
        var results = await JsonSerializer.DeserializeAsync<List<LrclibResponse>>(stream, JsonOptions, token);
        return results ?? (IReadOnlyList<LrclibResponse>)Array.Empty<LrclibResponse>();
    }
}
