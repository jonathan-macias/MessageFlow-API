using MessageFlow.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MessageFlow.Api.Observability;

/// <summary>
/// Alcanzabilidad de Evolution API. Nunca devuelve Unhealthy: la app sigue funcionando sin
/// el canal de WhatsApp, así que un fallo se reporta como Degraded (el /health responde 200
/// y el healthcheck del despliegue no tumba la versión). Cualquier respuesta HTTP, incluso
/// un 404, demuestra que el servicio está arriba.
/// </summary>
internal sealed class EvolutionHealthCheck(
    IHttpClientFactory httpClientFactory,
    EvolutionApiOptions options) : IHealthCheck
{
    // Menor que el --max-time de los healthchecks externos (deploy.sh usa 5 s): si Evolution no
    // responde, /health tarda como mucho esto, no más.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            return HealthCheckResult.Degraded("Evolution API no está configurada (EvolutionApi:BaseUrl).");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            using var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync(baseUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return HealthCheckResult.Degraded("Evolution API no responde.");
        }
    }
}
