using MessageFlow.Application.Abstractions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MessageFlow.Infrastructure.Scheduling;

/// <summary>
/// Worker de scheduling (Fase 8, §16): ciclo periódico de productor/consumidor sobre
/// IScheduledExecutionProcessor. No contiene lógica de negocio: cada tick crea un scope
/// de DI y delega; los errores se registran y el ciclo continúa (resiliencia ante
/// fallos transitorios de PostgreSQL). Seguro con múltiples instancias: la idempotencia
/// del productor y el reclamo atómico del consumidor lo garantizan (§17).
/// </summary>
public sealed class FlowSchedulerWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<FlowSchedulerWorker> logger,
    FlowSchedulingOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Scheduler deshabilitado por configuración; el worker no se inicia.");
            return;
        }

        var interval = TimeSpan.FromSeconds(
            Math.Clamp(options.IntervalSeconds, FlowSchedulingOptions.MinIntervalSeconds, FlowSchedulingOptions.MaxIntervalSeconds));

        logger.LogInformation("Scheduler iniciado con intervalo de {Interval} s.", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Fallo transitorio (p. ej., PostgreSQL caído): registrar y continuar.
                logger.LogError(ex, "El ciclo del scheduler falló; se reintenta en el próximo intervalo.");
            }
        }

        logger.LogInformation("Scheduler detenido.");
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IScheduledExecutionProcessor>();

        var enqueued = await processor.EnqueueDueExecutionsAsync(cancellationToken);
        var processed = await processor.ProcessQueuedExecutionsAsync(cancellationToken);

        if (enqueued > 0 || processed > 0)
        {
            logger.LogDebug("Ciclo completado: {Enqueued} encoladas, {Processed} procesadas.", enqueued, processed);
        }
    }
}
