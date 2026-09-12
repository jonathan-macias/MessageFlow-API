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
/// Errores: cualquier fallo HTTP (red, timeout, 4xx/5xx) se traduce a
/// DeliveryResult.Failure; el motor lo registra como resultado del registro y
/// continúa con el resto de la corrida (patrón por-fila existente).
/// </summary>
public sealed class EvolutionApiClient(
    HttpClient httpClient,
    EvolutionApiOptions options,
    ILogger<EvolutionApiClient> logger) : IMessageProvider
{
    public Channel Channel => Channel.WhatsApp;

    private HttpClient Http { get; } = Configure(httpClient, options);

    private static HttpClient Configure(HttpClient httpClient, EvolutionApiOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BaseUrl);

        var baseUri = options.BaseUrl.TrimEnd().EndsWith('/')
            ? new Uri(options.BaseUrl.TrimEnd())
            : new Uri(options.BaseUrl.TrimEnd() + "/");

        httpClient.BaseAddress = baseUri;
        httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300));

        // Renueva el header para que reconfiguraciones de opciones no lo dupliquen.
        httpClient.DefaultRequestHeaders.Remove("apiKey");
        httpClient.DefaultRequestHeaders.Add("apiKey", options.GlobalApiKey);

        return httpClient;
    }

    public async Task<DeliveryResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/message/sendText/{options.Instance}";

        try
        {
            var response = await httpClient.PostAsJsonAsync(
                endpoint,
                new SendTextRequest(message.Recipient.Destination, message.Body),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, cancellationToken);
                logger.LogWarning(
                    "Evolution API rechazó el envío a {Destination}: HTTP {StatusCode}. {Detail}",
                    message.Recipient.Destination, (int)response.StatusCode, detail);

                return DeliveryResult.Failure($"HTTP {(int)response.StatusCode}{detail}");
            }

            return MapSuccess(await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout del HttpClient (no cancelación del host): fallo del registro.
            logger.LogWarning("Timeout enviando a {Destination} vía Evolution API.", message.Recipient.Destination);
            return DeliveryResult.Failure("Timeout comunicándose con Evolution API.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red enviando a {Destination} vía Evolution API.", message.Recipient.Destination);
            return DeliveryResult.Failure($"Fallo de red: {ex.Message}");
        }
    }

    private static Domain.Messaging.DeliveryResult MapSuccess(JsonDocument? document)
    {
        // La respuesta exitosa incluye key.id como identificador del mensaje.
        var providerMessageId = document?.RootElement.TryGetProperty("key", out var key) == true
            && key.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;

        return Domain.Messaging.DeliveryResult.Success(providerMessageId);
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
