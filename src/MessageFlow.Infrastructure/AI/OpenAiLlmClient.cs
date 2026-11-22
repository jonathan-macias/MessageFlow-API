using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MessageFlow.Infrastructure.AI;

/// <summary>OpenAI Chat Completions implementation of <see cref="ILlmClient"/>.</summary>
public sealed class OpenAiLlmClient(
    HttpClient httpClient,
    OpenAiOptions options,
    ILogger<OpenAiLlmClient> logger) : ILlmClient
{
    public string Provider => AiOptions.OpenAi;

    public async Task<string> CompleteAsync(
        string systemInstruction,
        string prompt,
        LlmOutputFormat format = LlmOutputFormat.Json,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemInstruction },
                new JsonObject { ["role"] = "user", ["content"] = prompt },
            },
            ["max_completion_tokens"] = options.MaxOutputTokens,
        };

        // JSON mode only when a JSON object is expected: OpenAI rejects it (400) unless the messages
        // contain the word "JSON", which the free-text prompts do not.
        if (format == LlmOutputFormat.Json)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        if (options.SupportsTemperature())
        {
            body["temperature"] = 0.7;
        }

        var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? OpenAiOptions.DefaultBaseUrl : options.BaseUrl;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}/v1/chat/completions")
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("OpenAI API returned {StatusCode}: {Error}", response.StatusCode, errorContent);
                throw LlmHttp.ForError(response, errorContent);
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return ExtractText(json);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Failed to call OpenAI API.");
            throw new LlmProviderException(
                "Failed to communicate with the AI service. Try again later.",
                LlmFailureKind.Transient,
                innerException: ex);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "OpenAI API returned an unreadable body.");
            throw new LlmProviderException(
                "The AI service returned an unreadable response.",
                LlmFailureKind.Transient,
                innerException: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI API request timed out.");
            throw new LlmProviderException(
                "AI service request timed out. Please try again.",
                LlmFailureKind.Transient,
                innerException: ex);
        }
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new LlmProviderException("OpenAI API key is not configured.", LlmFailureKind.ProviderRejected);
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new LlmProviderException("OpenAI model is not configured.", LlmFailureKind.ProviderRejected);
        }
    }

    private string ExtractText(JsonElement response)
    {
        if (!response.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new LlmProviderException("AI service returned an empty response.", LlmFailureKind.RequestFailed);
        }

        var choice = choices[0];

        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            throw new LlmProviderException(
                "AI service returned an invalid response structure.", LlmFailureKind.RequestFailed);
        }

        if (message.TryGetProperty("refusal", out var refusal) &&
            refusal.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(refusal.GetString()))
        {
            logger.LogWarning("OpenAI refused the request.");
            throw new LlmProviderException("The AI service declined to answer this request.", LlmFailureKind.RequestFailed);
        }

        var finishReason = choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String
            ? finish.GetString()
            : null;

        if (finishReason == "length")
        {
            // A truncated JSON object is unusable; do not hand it to the parser.
            throw new LlmProviderException(
                "The AI service response was cut off before it finished.", LlmFailureKind.RequestFailed);
        }

        var text = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new LlmProviderException("AI service returned an empty message.", LlmFailureKind.RequestFailed);
        }

        return text;
    }
}
