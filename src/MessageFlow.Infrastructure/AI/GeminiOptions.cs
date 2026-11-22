namespace MessageFlow.Infrastructure.AI;

/// <summary>
/// Gemini API configuration. Bound from appsettings.json "Gemini" section.
/// Nothing is defaulted to a concrete model: if the section is missing, the service
/// fails with an explicit message instead of silently calling a hardcoded model.
/// </summary>
public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    /// <summary>Gemini API key. Never exposed to frontend.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Generative model for text (e.g., "gemini-3.5-flash-lite").
    /// Only needed when Gemini is the primary or fallback text provider (see <see cref="AiOptions"/>).
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Embedding model for semantic search (e.g., "gemini-embedding-001").</summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>
    /// Width of the embedding vector. Must match the database column
    /// <c>vector(n)</c>; validated at startup against
    /// <see cref="MessageFlow.Domain.Datasets.DatasetRowEmbedding.VectorDimensions"/>.
    /// </summary>
    public int EmbeddingDimensions { get; set; }

    /// <summary>
    /// Fails fast when the embedding settings are missing. Embeddings always use Gemini
    /// (vectors from different models are not comparable), regardless of the text provider.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException("Missing configuration 'Gemini:ApiKey'.");
        }

        if (string.IsNullOrWhiteSpace(EmbeddingModel))
        {
            throw new InvalidOperationException("Missing configuration 'Gemini:EmbeddingModel'.");
        }

        if (EmbeddingDimensions != MessageFlow.Domain.Datasets.DatasetRowEmbedding.VectorDimensions)
        {
            throw new InvalidOperationException(
                $"'Gemini:EmbeddingDimensions' is {EmbeddingDimensions} but the vector column is " +
                $"vector({MessageFlow.Domain.Datasets.DatasetRowEmbedding.VectorDimensions}). " +
                "Changing the width requires a new migration and reindexing all datasets.");
        }
    }

    /// <summary>Fails fast when Gemini is used for text generation but has no key or model.</summary>
    public void ValidateForTextGeneration()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException("Missing configuration 'Gemini:ApiKey'.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidOperationException("Missing configuration 'Gemini:Model'.");
        }
    }
}
