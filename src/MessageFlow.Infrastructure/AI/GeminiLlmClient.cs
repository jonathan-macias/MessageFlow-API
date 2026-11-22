using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MessageFlow.Infrastructure.AI;

/// <summary>Gemini <c>generateContent</c> implementation of <see cref="ILlmClient"/>.</summary>
public sealed class GeminiLlmClient(
    HttpClient httpClient,
    GeminiOptions options,
    ILogger<GeminiLlmClient> logger) : ILlmClient
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    public string Provider => AiOptions.Gemini;

    public async Task<string> CompleteAsync(
        string systemInstruction,
        string prompt,
        LlmOutputFormat format = LlmOutputFormat.Json,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        // Free-text answers must not be forced into JSON mode.
        var generationConfig = new Dictionary<string, object>
        {
            ["temperature"] = 0.7,
            ["max_output_tokens"] = 1024,
        };

        if (format == LlmOutputFormat.Json)
        {
            generationConfig["response_mime_type"] = "application/json";
        }

        var requestBody = new
        {
            system_instruction = new
            {
                parts = new[] { new { text = systemInstruction } }
            },
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = prompt } }
                }
            },
            generation_config = generationConfig
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BaseUrl}/models/{options.Model}:generateContent")
        {
            Content = JsonContent.Create(requestBody, options: new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            })
        };

        // Use x-goog-api-key header instead of query parameter
        request.Headers.Add("x-goog-api-key", options.ApiKey);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Gemini API returned {StatusCode}: {Error}", response.StatusCode, errorContent);
                throw LlmHttp.ForError(response, errorContent);
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return ExtractText(json);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Failed to call Gemini API.");
            throw new LlmProviderException(
                "Failed to communicate with the AI service. Try again later.",
                LlmFailureKind.Transient,
                innerException: ex);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Gemini API returned an unreadable body.");
            throw new LlmProviderException(
                "The AI service returned an unreadable response.",
                LlmFailureKind.Transient,
                innerException: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Gemini API request timed out.");
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
            throw new LlmProviderException("Gemini API key is not configured.", LlmFailureKind.ProviderRejected);
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new LlmProviderException("Gemini model is not configured.", LlmFailureKind.ProviderRejected);
        }
    }

    private static string ExtractText(JsonElement response)
    {
        if (!response.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            throw new LlmProviderException("AI service returned an empty response.", LlmFailureKind.RequestFailed);
        }

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array ||
            parts.GetArrayLength() == 0)
        {
            throw new LlmProviderException(
                "AI service returned an invalid response structure.", LlmFailureKind.RequestFailed);
        }

        var text = parts[0].TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new LlmProviderException("AI service returned an empty message.", LlmFailureKind.RequestFailed);
        }

        return text;
    }
}
