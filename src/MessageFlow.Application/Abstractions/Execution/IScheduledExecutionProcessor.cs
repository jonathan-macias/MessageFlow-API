namespace MessageFlow.Application.Abstractions.Execution;

/// <summary>
/// Orquestador del ciclo de scheduling (Fase 8): produce ejecuciones pendientes cuando
/// corresponde (reloj o trigger de datos) y consume todas las pendientes vía el motor.
/// El worker de Infrastructure lo invoca periódicamente; también es invocable a demanda
/// (p. ej., un endpoint administrativo en Fase 9).
/// </summary>
public interface IScheduledExecutionProcessor
{
    /// <summary>
    /// Crea las FlowExecution pendientes cuya ocurrencia ya venció (OneTime/Recurring)
    /// o cuyo trigger de datos coincide con la fecha local actual (DataTriggered).
    /// Idempotente: claves deterministas + constraint único impiden duplicados aunque
    /// varias instancias compitan. Devuelve cuántas ejecuciones creó.
    /// </summary>
    Task<int> EnqueueDueExecutionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Procesa un lote de ejecuciones Pending (manuales y programadas) con el motor,
    /// aislando fallos: una ejecución problemática no detiene al resto.
    /// Devuelve cuántas se procesaron hasta estado terminal.
    /// </summary>
    Task<int> ProcessQueuedExecutionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Ciclo completo del scheduler: producir y luego consumir.</summary>
    Task RunOnceAsync(CancellationToken cancellationToken = default);
}

/// <summary>Ajustes del scheduler. Registrado como singleton; configurable en Fase 9.</summary>
public sealed class FlowSchedulingOptions
{
    public const int MinIntervalSeconds = 1;
    public const int MaxIntervalSeconds = 3600;

    /// <summary>Permite apagar el scheduler por configuración (p. ej., instancias dedicadas solo a API).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Intervalo entre ciclos del scheduler, en segundos.</summary>
    public int IntervalSeconds { get; init; } = 60;

    /// <summary>Máximo de ejecuciones pendientes consumidas por ciclo.</summary>
    public int ConsumerBatchSize { get; init; } = 20;

    /// <summary>
    /// Máximo de ocurrencias vencidas que se encolan por Flow en un ciclo
    /// (recuperación tras inactividad). Evita crear cientos de ejecuciones
    /// retroactivas si la API estuvo caída mucho tiempo.
    /// </summary>
    public int MaxCatchUpOccurrences { get; init; } = 5;
}
