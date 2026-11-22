using System.Net;
using System.Net.Http.Headers;

namespace MessageFlow.Infrastructure.AI;

/// <summary>Failure classification shared by the HTTP-based <see cref="ILlmClient"/> implementations.</summary>
internal static class LlmHttp
{
    /// <summary>
    /// Maps an error response to what the caller should do next. A 429 is normally a rate limit
    /// (worth retrying), but OpenAI also answers 429 for an exhausted quota, which no retry fixes.
    /// </summary>
    public static LlmFailureKind Classify(HttpStatusCode status, string? body)
    {
        var code = (int)status;

        if (status == HttpStatusCode.TooManyRequests)
        {
            return body is not null && body.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
                ? LlmFailureKind.ProviderRejected
                : LlmFailureKind.Transient;
        }

        if (status == HttpStatusCode.RequestTimeout || code >= 500)
        {
            return LlmFailureKind.Transient;
        }

        return status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            ? LlmFailureKind.ProviderRejected
            : LlmFailureKind.RequestFailed;
    }

    public static TimeSpan? ParseRetryAfter(HttpResponseHeaders headers)
    {
        var retryAfter = headers.RetryAfter;

        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }

        return null;
    }

    public static LlmProviderException ForError(HttpResponseMessage response, string? body)
        => new(
            GeminiErrors.ToClientMessage(response.StatusCode),
            Classify(response.StatusCode, body),
            ParseRetryAfter(response.Headers));
}
