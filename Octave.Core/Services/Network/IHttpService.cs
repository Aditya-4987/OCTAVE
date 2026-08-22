using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Octave.Core.Services.Network;

public interface IHttpService
{
    Task<HttpResult<string>> GetStringAsync(
        string url,
        string? providerKey = null,
        IDictionary<string, string>? customHeaders = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    Task<HttpResult<byte[]>> GetByteArrayAsync(
        string url,
        string? providerKey = null,
        IDictionary<string, string>? customHeaders = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    Task<HttpResult<T>> GetJsonAsync<T>(
        string url,
        string? providerKey = null,
        IDictionary<string, string>? customHeaders = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    Task<HttpResult<HttpResponseMessage>> SendAsync(
        HttpRequestMessage request,
        string? providerKey = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);
}
