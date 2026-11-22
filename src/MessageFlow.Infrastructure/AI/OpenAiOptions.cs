namespace MessageFlow.Infrastructure.AI;

/// <summary>
/// OpenAI configuration. Bound from appsettings.json "OpenAI" section. The key belongs in the
/// environment (<c>OpenAI__ApiKey</c>), never in a committed file.
/// </summary>
public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    public const string DefaultBaseUrl = "https://api.openai.com";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Chat model for text generation (e.g. "gpt-4.1-mini"). Not defaulted on purpose.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>API root; overridable for tests or a compatible gateway.</summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>Upper bound for generated tokens.</summary>
    public int MaxOutputTokens { get; set; } = 1024;

    /// <summary>
    /// Reasoning models (o-series, gpt-5*) reject a custom <c>temperature</c>; for them the
    /// parameter is omitted instead of failing every request with a 400.
    /// </summary>
    public bool SupportsTemperature()
    {
        var model = Model.Trim().ToLowerInvariant();

        if (model.StartsWith("gpt-5", StringComparison.Ordinal))
        {
            return false;
        }

        return !(model.Length > 1 && model[0] == 'o' && char.IsDigit(model[1]));
    }

    /// <summary>Fails fast when OpenAI is used for text generation but has no key or model.</summary>
    public void ValidateForTextGeneration()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException("Missing configuration 'OpenAI:ApiKey'.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidOperationException("Missing configuration 'OpenAI:Model'.");
        }
    }
}
