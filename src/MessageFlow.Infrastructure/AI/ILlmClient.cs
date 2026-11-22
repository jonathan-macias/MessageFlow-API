using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Infrastructure.AI;

/// <summary>
/// Strategy for one text-generation provider. It only knows how to send a system instruction
/// plus a prompt and return the model's text (JSON already requested); prompts and response
/// parsing live in <see cref="LlmGenerativeAIService"/> and are shared by every provider.
/// </summary>
public interface ILlmClient
{
    /// <summary>Provider name used in logs and metrics ("Gemini", "OpenAI").</summary>
    string Provider { get; }

    /// <param name="format">
    /// <see cref="LlmOutputFormat.Json"/> asks the provider for a JSON object (the prompt must say so);
    /// <see cref="LlmOutputFormat.Text"/> is for free-text answers.
    /// </param>
    /// <exception cref="LlmProviderException">The provider failed; <see cref="LlmProviderException.Kind"/> says how.</exception>
    Task<string> CompleteAsync(
        string systemInstruction,
        string prompt,
        LlmOutputFormat format = LlmOutputFormat.Json,
        CancellationToken cancellationToken = default);
}

/// <summary>Shape of the answer the caller expects from the model.</summary>
public enum LlmOutputFormat
{
    /// <summary>A JSON object (JSON mode). OpenAI requires the word "JSON" in the messages for this mode.</summary>
    Json = 1,

    /// <summary>Plain text, no JSON mode.</summary>
    Text = 2,
}

/// <summary>What a provider failure means for retrying and for falling back.</summary>
public enum LlmFailureKind
{
    /// <summary>Overload, rate limit, timeout or network error: retry once, then fall back.</summary>
    Transient = 1,

    /// <summary>
    /// The provider cannot serve us (bad/missing key, exhausted quota, unknown model): retrying is
    /// pointless but the other provider may still work.
    /// </summary>
    ProviderRejected = 2,

    /// <summary>The request or the answer is bad (malformed request, empty or unusable output): another provider will not help.</summary>
    RequestFailed = 3,
}

/// <summary>
/// A provider failure. The message is safe to show to the API client; raw provider bodies are
/// only written to the log.
/// </summary>
public sealed class LlmProviderException(
    string message,
    LlmFailureKind kind,
    TimeSpan? retryAfter = null,
    Exception? innerException = null)
    : DomainException(message, innerException!)
{
    public LlmFailureKind Kind { get; } = kind;

    /// <summary>Delay requested by the provider (Retry-After), when it sent one.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
