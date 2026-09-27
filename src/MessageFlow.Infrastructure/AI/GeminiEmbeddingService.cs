using System.Net.Http.Json;
using System.Text.Json;
using MessageFlow.Application.Abstractions;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace MessageFlow.Infrastructure.AI;

/// <summary>
/// Gemini implementation of <see cref="IEmbeddingService"/>.
///
/// Uses the batch endpoint (<c>batchEmbedContents</c>) for indexing because it costs
/// roughly half of individual calls and issues far fewer requests, which matters on the
/// free tier where the requests-per-minute limit is the bottleneck.
///
/// Two API shapes are used on purpose:
///   - <c>embedContent</c> for the single user question.
///   - <c>batchEmbedContents</c> for bulk indexing of dataset rows.
/// </summary>
public sealed class GeminiEmbeddingService(
    HttpClient httpClient,
    GeminiOptions options,
    ILogger<GeminiEmbeddingService> logger) : IEmbeddingService
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    /// <summary>
    /// Hard cap the API accepts per batch call. The configured batch size is validated
    /// against this so a misconfiguration fails locally instead of as a 400 from Google.
    /// </summary>
    public const int MaxBatchSize = 100;

    public int Dimensions => options.EmbeddingDimensions;

    public async Task<float[]> GenerateEmbeddingAsync(
        string text,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        EnsureConfigured();

        var url = $"{BaseUrl}/models/{options.EmbeddingModel}:embedContent";

        var requestBody = new
        {
            model = $"models/{options.EmbeddingModel}",
            content = new { parts = new[] { new { text } } },
            taskType = TaskType(purpose),
            outputDimensionality = options.EmbeddingDimensions,
        };

        using var response = await SendAsync(url, requestBody, cancellationToken);

        var payload = await ReadPayloadAsync(response, cancellationToken);

        if (!payload.TryGetProperty("embedding", out var embedding) ||
            !embedding.TryGetProperty("values", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            throw new DomainException("The AI service returned an invalid embedding response.");
        }

        return ReadVector(values);
    }

    public async Task<IReadOnlyList<float[]>> GenerateBatchAsync(
        IReadOnlyList<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0)
        {
            return [];
        }

        if (texts.Count > MaxBatchSize)
        {
            throw new DomainException(
                $"A batch cannot exceed {MaxBatchSize} texts; received {texts.Count}.");
        }

        EnsureConfigured();

        var url = $"{BaseUrl}/models/{options.EmbeddingModel}:batchEmbedContents";

        var requestBody = new
        {
            requests = texts.Select(text => new
            {
                model = $"models/{options.EmbeddingModel}",
                content = new { parts = new[] { new { text } } },
                taskType = TaskType(purpose),
                outputDimensionality = options.EmbeddingDimensions,
            }).ToArray(),
        };

        using var response = await SendAsync(url, requestBody, cancellationToken);

        var payload = await ReadPayloadAsync(response, cancellationToken);

        if (!payload.TryGetProperty("embeddings", out var embeddings) ||
            embeddings.ValueKind != JsonValueKind.Array)
        {
            throw new DomainException("The AI service returned an invalid batch embedding response.");
        }

        var returned = embeddings.EnumerateArray()
            .Select(element => element.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array
                ? (Text: element.TryGetProperty("text", out var text) ? text.GetString() : null, Vector: ReadVector(values))
                : default)
            .Where(item => item.Vector is not null)
            .ToList();

        if (returned.Count != texts.Count)
        {
            throw new DomainException(
                $"The AI service returned {returned.Count} embeddings for {texts.Count} texts.");
        }

        // Normally the API echoes the input text alongside each vector, which makes the
        // match by content exact and immune to reordering. If it does not echo it, fall
        // back to positional order, which is what the API documents.
        var echoed = returned.All(item => item.Text is not null);
        var byContent = new Dictionary<string, Queue<float[]>>(StringComparer.Ordinal);

        if (echoed)
        {
            foreach (var (text, vector) in returned)
            {
                if (!byContent.TryGetValue(text!, out var queue))
                {
                    queue = new Queue<float[]>();
                    byContent[text!] = queue;
                }

                queue.Enqueue(vector!);
            }
        }

        var result = new float[texts.Count][];

        for (var i = 0; i < texts.Count; i++)
        {
            if (echoed)
            {
                if (byContent.TryGetValue(texts[i], out var queue) && queue.Count > 0)
                {
                    result[i] = queue.Dequeue();
                    continue;
                }

                throw new DomainException(
                    "The AI service did not return an embedding for every text sent.");
            }

            result[i] = returned[i].Vector!;
        }

        return result;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string url,
        object requestBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestBody)
        };

        request.Headers.Add("x-goog-api-key", options.ApiKey);

        try
        {
            return await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Failed to call the Gemini embeddings endpoint.");
            throw new DomainException($"Failed to communicate with AI service: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("Gemini embeddings request timed out.");
            throw new DomainException("AI service request timed out. Please try again.");
        }
    }

    private async Task<JsonElement> ReadPayloadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Gemini embeddings returned {StatusCode}: {Error}", response.StatusCode, errorContent);
            throw new DomainException($"AI service returned {(int)response.StatusCode}: {errorContent}");
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    private float[] ReadVector(JsonElement values)
    {
        var vector = new float[values.GetArrayLength()];

        if (vector.Length != options.EmbeddingDimensions)
        {
            throw new DomainException(
                $"The AI service returned a vector of {vector.Length} dimensions but " +
                $"{options.EmbeddingDimensions} were configured.");
        }

        var index = 0;

        foreach (var value in values.EnumerateArray())
        {
            vector[index++] = (float)value.GetDouble();
        }

        return vector;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new DomainException("Gemini API key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.EmbeddingModel))
        {
            throw new DomainException("Gemini embedding model is not configured.");
        }

        if (options.EmbeddingDimensions != DatasetRowEmbedding.VectorDimensions)
        {
            throw new DomainException(
                $"'Gemini:EmbeddingDimensions' is {options.EmbeddingDimensions} but the database column is " +
                $"vector({DatasetRowEmbedding.VectorDimensions}). Reindex after changing the configuration.");
        }
    }

    /// <summary>
    /// Gemini expects a UPPER_SNAKE task type. Optimizing separately for documents and
    /// queries measurably improves retrieval quality over using a single task for both.
    /// </summary>
    private static string TaskType(EmbeddingPurpose purpose) => purpose switch
    {
        EmbeddingPurpose.Document => "RETRIEVAL_DOCUMENT",
        EmbeddingPurpose.Query => "RETRIEVAL_QUERY",
        _ => "RETRIEVAL_DOCUMENT",
    };
}
