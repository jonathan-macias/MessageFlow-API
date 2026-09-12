using MessageFlow.Domain.Enums;

namespace MessageFlow.Infrastructure.Messaging;

/// <summary>
/// Credenciales y endpoint del canal WhatsApp vía Evolution API (§30-§32).
/// Se enlaza desde la sección "EvolutionApi" de appsettings; nunca hardcodeado.
/// </summary>
public sealed class EvolutionApiOptions
{
    public const string SectionName = "EvolutionApi";

    /// <summary>Raíz del servicio, p. ej. "https://evolution.mi-servidor.com".</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Instancia de WhatsApp que envía los mensajes.</summary>
    public string Instance { get; init; } = string.Empty;

    /// <summary>ApiKey global (header "apiKey").</summary>
    public string GlobalApiKey { get; init; } = string.Empty;

    /// <summary>Timeout por petición, en segundos.</summary>
    public int TimeoutSeconds { get; init; } = 30;
}
