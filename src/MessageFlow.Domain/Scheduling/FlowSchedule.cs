using MessageFlow.Domain.Common;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Scheduling;

/// <summary>
/// Value Object serializable (jsonb) que describe CUÁNDO se ejecuta un Flow.
/// Siempre almacena explícitamente la zona horaria del usuario; los instantes
/// absolutos (<see cref="RunAtUtc"/>) se guardan en UTC.
/// </summary>
public sealed record FlowSchedule
{
    private FlowSchedule(ExecutionType type, TimeZoneId zone, DateTimeOffset? runAtUtc, RecurrenceRule? recurrence)
        => (Type, Zone, RunAtUtc, Recurrence) = (type, zone, runAtUtc, recurrence);

    public ExecutionType Type { get; }

    /// <summary>Zona horaria configurada por el usuario (IANA), para calcular ocurrencias locales.</summary>
    public TimeZoneId Zone { get; }

    /// <summary>Instante único de ejecución (solo OneTime). UTC.</summary>
    public DateTimeOffset? RunAtUtc { get; }

    /// <summary>Regla de recurrencia (solo Recurring).</summary>
    public RecurrenceRule? Recurrence { get; }

    public static FlowSchedule OneTime(DateTimeOffset runAtUtc, TimeZoneId zone)
        => new(ExecutionType.OneTime, zone, runAtUtc.RequireUtc(), null);

    public static FlowSchedule Recurring(RecurrenceRule recurrence, TimeZoneId zone)
    {
        ArgumentNullException.ThrowIfNull(recurrence);
        return new(ExecutionType.Recurring, zone, null, recurrence);
    }

    public static FlowSchedule DataTriggered(TimeZoneId zone)
        => new(ExecutionType.DataTriggered, zone, null, null);

    public static FlowSchedule Immediate(TimeZoneId zone)
        => new(ExecutionType.Immediate, zone, null, null);

    /// <summary>
    /// Próxima ejecución programada estrictamente posterior a <paramref name="afterUtc"/> (UTC),
    /// o null si el tipo no se programa por reloj (Immediate / DataTriggered).
    /// Para OneTime devuelve su instante solo si aún no venció (el scheduler lo consume una vez).
    /// </summary>
    public DateTimeOffset? NextRunAfter(DateTimeOffset afterUtc)
    {
        var utc = afterUtc.RequireUtc();

        return Type switch
        {
            ExecutionType.OneTime when RunAtUtc.HasValue && RunAtUtc.Value > utc => RunAtUtc,
            ExecutionType.Recurring => Recurrence!.NextOccurrenceAfter(utc, Zone),
            _ => null,
        };
    }
}
