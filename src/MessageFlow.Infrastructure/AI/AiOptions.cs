namespace MessageFlow.Infrastructure.AI;

/// <summary>
/// Which provider answers text-generation requests, and which one takes over when it is
/// overloaded. Bound from the "Ai" section. Embeddings are not affected: they always use Gemini.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public const string Gemini = "Gemini";
    public const string OpenAi = "OpenAI";

    /// <summary>Primary text provider: "Gemini" or "OpenAI".</summary>
    public string Provider { get; set; } = Gemini;

    /// <summary>Provider used when the primary fails; empty disables the fallback.</summary>
    public string FallbackProvider { get; set; } = string.Empty;

    public string NormalizedProvider => Normalize(Provider)!;

    public string? NormalizedFallbackProvider => Normalize(FallbackProvider);

    /// <summary>Fails at startup on an unknown provider name or when both roles are the same provider.</summary>
    public void Validate()
    {
        if (Normalize(Provider) is null)
        {
            throw new InvalidOperationException(
                $"'Ai:Provider' must be '{Gemini}' or '{OpenAi}' (was '{Provider}').");
        }

        if (!string.IsNullOrWhiteSpace(FallbackProvider) && Normalize(FallbackProvider) is null)
        {
            throw new InvalidOperationException(
                $"'Ai:FallbackProvider' must be empty, '{Gemini}' or '{OpenAi}' (was '{FallbackProvider}').");
        }

        if (NormalizedFallbackProvider is { } fallback && fallback == NormalizedProvider)
        {
            throw new InvalidOperationException(
                "'Ai:FallbackProvider' must differ from 'Ai:Provider'.");
        }
    }

    /// <summary>Validates the key/model of every provider this configuration can actually call.</summary>
    public void ValidateProviders(GeminiOptions gemini, OpenAiOptions openAi)
    {
        Validate();

        foreach (var provider in new[] { NormalizedProvider, NormalizedFallbackProvider })
        {
            switch (provider)
            {
                case Gemini:
                    gemini.ValidateForTextGeneration();
                    break;
                case OpenAi:
                    openAi.ValidateForTextGeneration();
                    break;
            }
        }
    }

    private static string? Normalize(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "gemini" => Gemini,
        "openai" => OpenAi,
        _ => null,
    };
}
