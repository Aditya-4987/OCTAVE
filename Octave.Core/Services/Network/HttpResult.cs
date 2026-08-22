using System;
using System.Net;

namespace Octave.Core.Services.Network;

public record HttpResult<T>(
    bool IsSuccess,
    HttpStatusCode? StatusCode,
    T? Data,
    string? ErrorMessage,
    TimeSpan? RetryAfter = null
)
{
    public static HttpResult<T> Success(T data, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(true, statusCode, data, null, null);

    public static HttpResult<T> Failure(string errorMessage, HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null) =>
        new(false, statusCode, default, errorMessage, retryAfter);

    public static HttpResult<T> Cancelled() =>
        new(false, null, default, "The operation was cancelled.", null);
}
