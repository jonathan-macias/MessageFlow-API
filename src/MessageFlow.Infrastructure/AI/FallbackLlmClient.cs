using Microsoft.Extensions.Logging;

namespace MessageFlow.Infrastructure.AI;

/// <summary>
/// Decorator that keeps text generation working when the primary provider is overloaded:
/// a transient failure is retried once, and if the primary still cannot serve the request
/// (or rejects us outright, e.g. exhausted quota) the secondary provider takes over.
///
/// A bad request or an unusable answer (<see cref="LlmFailureKind.RequestFailed"/>) is not a
/// provider problem, so it is returned as is instead of paying for a second call.
/// </summary>
public sealed class FallbackLlmClient : ILlmClient
{
    /// <summary>Longest Retry-After the client is willing to wait for before giving up on a provider.</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(5);

    private readonly ILlmClient _primary;
    private readonly ILlmClient? _secondary;
    private readonly TimeSpan _retryDelay;
    private readonly ILogger<FallbackLlmClient> _logger;

    public FallbackLlmClient(
        ILlmClient primary,
        ILlmClient? secondary,
        ILogger<FallbackLlmClient> logger,
        TimeSpan? retryDelay = null)
    {
        _primary = primary;
        _secondary = secondary;
        _logger = logger;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(1500);
    }

    public string Provider => _secondary is null ? _primary.Provider : $"{_primary.Provider}+{_secondary.Provider}";

    public async Task<string> CompleteAsync(
        string systemInstruction,
        string prompt,
        LlmOutputFormat format = LlmOutputFormat.Json,
        CancellationToken cancellationToken = default)
    {
        LlmProviderException primaryFailure;

        try
        {
            return await TryProviderAsync(_primary, systemInstruction, prompt, format, cancellationToken);
        }
        catch (LlmProviderException ex) when (ex.Kind != LlmFailureKind.RequestFailed && _secondary is not null)
        {
            primaryFailure = ex;
        }

        // Only reached with a secondary: without one the exception above propagates untouched,
        // keeping its client-friendly message (e.g. "usage limit reached").
        _logger.LogWarning(
            "AI provider {Primary} failed ({Kind}); falling back to {Secondary}.",
            _primary.Provider,
            primaryFailure.Kind,
            _secondary!.Provider);
        AiMetrics.RecordFallback(_primary.Provider, _secondary.Provider);

        try
        {
            return await TryProviderAsync(_secondary, systemInstruction, prompt, format, cancellationToken);
        }
        catch (LlmProviderException ex) when (ex.Kind != LlmFailureKind.RequestFailed)
        {
            _logger.LogError(
                "AI fallback provider {Secondary} also failed ({Kind}); no provider could serve the request.",
                _secondary.Provider,
                ex.Kind);

            throw new LlmProviderException(
                "The AI service is temporarily unavailable. Try again in a few minutes.",
                LlmFailureKind.Transient,
                innerException: ex);
        }
    }

    /// <summary>One provider: a single call, plus one retry when the failure is transient.</summary>
    private async Task<string> TryProviderAsync(
        ILlmClient client,
        string systemInstruction,
        string prompt,
        LlmOutputFormat format,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(client, systemInstruction, prompt, format, cancellationToken);
        }
        catch (LlmProviderException ex) when (ex.Kind == LlmFailureKind.Transient)
        {
            var delay = ex.RetryAfter is { } requested && requested < MaxRetryDelay ? requested : _retryDelay;

            _logger.LogWarning(
                "AI provider {Provider} failed transiently; retrying once in {DelayMs} ms.",
                client.Provider,
                (int)delay.TotalMilliseconds);

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return await CallAsync(client, systemInstruction, prompt, format, cancellationToken);
        }
    }

    private static async Task<string> CallAsync(
        ILlmClient client,
        string systemInstruction,
        string prompt,
        LlmOutputFormat format,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = await client.CompleteAsync(systemInstruction, prompt, format, cancellationToken);
            AiMetrics.RecordRequest(client.Provider, "success");
            return text;
        }
        catch (LlmProviderException ex)
        {
            AiMetrics.RecordRequest(client.Provider, ex.Kind switch
            {
                LlmFailureKind.Transient => "transient_failure",
                LlmFailureKind.ProviderRejected => "rejected",
                _ => "failed",
            });
            throw;
        }
    }
}
