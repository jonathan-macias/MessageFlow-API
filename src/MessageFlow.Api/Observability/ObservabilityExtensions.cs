using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace MessageFlow.Api.Observability;

/// <summary>
/// Monitoreo para Sentinel: métricas OpenTelemetry en formato Prometheus (/metrics),
/// salud de PostgreSQL (/health) y los últimos errores 5xx (/ops/recent-errors).
/// </summary>
public static class ObservabilityExtensions
{
    public const string OpsKeyHeader = "X-Ops-Key";

    public static IServiceCollection AddMessageFlowObservability(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton(new RecentErrorsBuffer());

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("messageflow-api"))
            .WithMetrics(metrics => metrics
                // http_server_request_duration_seconds: throughput, % 5xx y p95 por ruta.
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                // Npgsql publica sus métricas (conexiones, comandos) en este Meter.
                .AddMeter("Npgsql")
                .AddPrometheusExporter());

        services.AddHealthChecks()
            .AddNpgSql(connectionString, name: "postgresql", timeout: TimeSpan.FromSeconds(3));

        return services;
    }

    public static WebApplication MapMessageFlowObservability(this WebApplication app)
    {
        // El FallbackPolicy exige autenticación en todo; Prometheus y Sentinel no tienen JWT.
        app.MapPrometheusScrapingEndpoint().AllowAnonymous();

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResultStatusCodes =
            {
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status200OK,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        }).AllowAnonymous();

        // Sin clave configurada el endpoint no existe: así nunca queda abierto por descuido.
        var opsKey = app.Configuration["Ops:ApiKey"];
        if (!string.IsNullOrWhiteSpace(opsKey))
        {
            app.MapGet("/ops/recent-errors", (HttpContext http, RecentErrorsBuffer buffer, int? minutes, int? max) =>
            {
                if (http.Request.Headers[OpsKeyHeader] != opsKey)
                {
                    return Results.Unauthorized();
                }

                var since = DateTimeOffset.UtcNow.AddMinutes(-Math.Clamp(minutes ?? 15, 1, 1440));
                return Results.Ok(buffer.Since(since, Math.Clamp(max ?? 50, 1, 200)));
            }).AllowAnonymous().ExcludeFromDescription();
        }

        if (app.Environment.IsDevelopment())
        {
            // Solo para la demo: provocan incidentes R1 (5xx) y R2 (latencia) a voluntad.
            app.MapGet("/dev/fail", () =>
            {
                throw new InvalidOperationException("Fallo simulado para probar Sentinel.");
            }).AllowAnonymous().ExcludeFromDescription();

            app.MapGet("/dev/slow", async (int? ms) =>
            {
                await Task.Delay(Math.Clamp(ms ?? 2000, 0, 30000));
                return Results.Ok(new { slept = ms ?? 2000 });
            }).AllowAnonymous().ExcludeFromDescription();
        }

        return app;
    }
}
