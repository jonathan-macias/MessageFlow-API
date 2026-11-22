using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
            .AddNpgSql(connectionString, name: "postgresql", timeout: TimeSpan.FromSeconds(3))
            // Evolution caída no tumba la app: se reporta Degraded (el /health sigue en 200).
            .AddCheck<EvolutionHealthCheck>("evolution", failureStatus: HealthStatus.Degraded);

        return services;
    }

    /// <summary>
    /// /metrics no debe ser público. Las peticiones que llegan por el túnel de Cloudflare
    /// (cabecera Cf-Connecting-Ip) solo pasan con la clave de operaciones; el scraping local,
    /// directo al puerto del contenedor, sigue funcionando sin ella. Para conservar el acceso
    /// público anterior, configurar Observability:PublicMetrics=true.
    /// </summary>
    public static WebApplication UseMessageFlowMetricsProtection(this WebApplication app)
    {
        var publicMetrics = app.Configuration.GetValue<bool>("Observability:PublicMetrics");
        var opsKey = app.Configuration["Ops:ApiKey"];

        if (publicMetrics)
        {
            return app;
        }

        app.Use(async (context, next) =>
        {
            var viaPublicTunnel = context.Request.Headers.ContainsKey("Cf-Connecting-Ip");

            if (viaPublicTunnel
                && context.Request.Path.StartsWithSegments("/metrics")
                && !KeyMatches(context.Request.Headers[OpsKeyHeader].ToString(), opsKey))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });

        return app;
    }

    private static bool KeyMatches(string provided, string? expected)
        => !string.IsNullOrWhiteSpace(expected)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided),
                Encoding.UTF8.GetBytes(expected));

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
                if (!KeyMatches(http.Request.Headers[OpsKeyHeader].ToString(), opsKey))
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
