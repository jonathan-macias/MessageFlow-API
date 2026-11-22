using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Messaging;
using Microsoft.Extensions.Logging;

namespace MessageFlow.Infrastructure.Messaging;

/// <summary>
/// Proveedor real de WhatsApp sobre Evolution API (§30-§32). Su ÚNICA responsabilidad
/// es la comunicación HTTP: POST /message/sendText/{instance} con header "apiKey".
/// Se autoconfigura desde <see cref="EvolutionApiOptions"/> (BaseUrl, instancia,
/// credenciales y timeout) — nada hardcodeado ni dependiente del wiring de DI.
/// No conoce Flows, datasets ni plantillas — recibe el mensaje ya construido.
///
/// Errores: ninguna condición (configuración ausente, red, timeout, 4xx/5xx) lanza una
/// excepción al motor: todas se traducen a DeliveryResult.Failure y se registran en el log.
/// Los fallos de infraestructura se marcan como transitorios para que el motor detenga la
/// corrida si el proveedor está caído, en lugar de intentar cada fila. Los teléfonos se
/// enmascaran en los logs.
/// </summary>
public sealed class EvolutionApiClient(
    HttpClient httpClient,
    EvolutionApiOptions options,
    ILogger<EvolutionApiClient> logger) : IMessageProvider
{
    private readonly bool _isConfigured = TryConfigure(httpClient, options);

    public Channel Channel => Channel.WhatsApp;

    /// <summary>
    /// Devuelve false (sin lanzar) si falta configuración: así la aplicación arranca y el
    /// scheduler sigue vivo aunque Evolution API no esté disponible o configurada.
    /// </summary>
    private static bool TryConfigure(HttpClient httpClient, EvolutionApiOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || string.IsNullOrWhiteSpace(options.Instance)
            || string.IsNullOrWhiteSpace(options.GlobalApiKey))
        {
            return false;
        }

        try
        {
            var baseUri = options.BaseUrl.TrimEnd().EndsWith('/')
                ? new Uri(options.BaseUrl.TrimEnd())
                : new Uri(options.BaseUrl.TrimEnd() + "/");

            httpClient.BaseAddress = baseUri;
            httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300));

            // Renueva el header para que reconfiguraciones de opciones no lo dupliquen.
            httpClient.DefaultRequestHeaders.Remove("apiKey");
            httpClient.DefaultRequestHeaders.Add("apiKey", options.GlobalApiKey);

            return true;
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public async Task<DeliveryResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (!_isConfigured)
        {
            logger.LogError(
                "Evolution API no está configurada o su configuración es inválida (EvolutionApi:BaseUrl, Instance y GlobalApiKey).");
            return DeliveryResult.Failure("Evolution API is not configured.", isTransient: true);
        }

        var destination = PhoneMasker.Mask(message.Recipient.Destination);
        var endpoint = $"/message/sendText/{options.Instance}";

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                endpoint,
                new SendTextRequest(message.Recipient.Destination, message.Body),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken);
                var transient = IsTransient(response.StatusCode);

                logger.LogWarning(
                    "Evolution API rechazó el envío a {Destination}: HTTP {StatusCode} ({FailureKind}).{Detail}",
                    destination, (int)response.StatusCode, transient ? "transitorio" : "permanente",
                    PhoneMasker.MaskDigits(detail));

                return DeliveryResult.Failure($"HTTP {(int)response.StatusCode}{detail}", transient);
            }

            return await MapSuccessAsync(response, destination, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout del HttpClient (no cancelación del host): fallo transitorio.
            logger.LogWarning("Timeout enviando a {Destination} vía Evolution API.", destination);
            return DeliveryResult.Failure("Timed out communicating with Evolution API.", isTransient: true);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red enviando a {Destination} vía Evolution API.", destination);
            return DeliveryResult.Failure($"Network error: {ex.Message}", isTransient: true);
        }
    }

    /// <summary>
    /// Proveedor caído o mal configurado (5xx, límite de uso, credenciales o instancia
    /// inválidas) frente a un rechazo propio del mensaje o del número (resto de 4xx).
    /// </summary>
    private static bool IsTransient(HttpStatusCode status)
        => (int)status >= 500
            || status is HttpStatusCode.TooManyRequests
                or HttpStatusCode.RequestTimeout
                or HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound;

    private async Task<DeliveryResult> MapSuccessAsync(
        HttpResponseMessage response,
        string maskedDestination,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken);

            // La respuesta exitosa incluye key.id como identificador del mensaje.
            var providerMessageId = document?.RootElement.TryGetProperty("key", out var key) == true
                && key.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;

            return DeliveryResult.Success(providerMessageId);
        }
        catch (JsonException ex)
        {
            // El proveedor respondió 2xx: el mensaje probablemente salió. Marcarlo como fallo
            // provocaría un reenvío duplicado, así que se registra el aviso y se da por entregado.
            logger.LogWarning(ex,
                "Evolution API respondió 2xx a {Destination} con un cuerpo ilegible; se considera enviado sin identificador.",
                maskedDestination);

            return DeliveryResult.Success(null);
        }
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return string.IsNullOrWhiteSpace(body)
                ? string.Empty
                : $" {Truncate(body.Trim(), 300)}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private sealed record SendTextRequest(string Number, string Text);
}
