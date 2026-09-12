using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Scheduling;

/// <summary>
/// Regla de recurrencia serializable (jsonb). Extensible: agregar Weekly/Monthly/Cron
/// requiere nuevos miembros de <see cref="Enums.RecurrenceFrequency"/> + lógica en
/// <see cref="NextOccurrenceAfter"/>, sin cambios estructurales.
/// </summary>
public sealed record RecurrenceRule
{
    public const int MinIntervalDays = 1;
    public const int MaxIntervalDays = 365;

    public Enums.RecurrenceFrequency Frequency { get; }

    /// <summary>Hora local del día (en la zona del schedule) a la que debe ejecutarse.</summary>
    public TimeOnly LocalTimeOfDay { get; }

    /// <summary>Intervalo en días (1 = todos los días).</summary>
    public int IntervalDays { get; }

    private RecurrenceRule(Enums.RecurrenceFrequency frequency, TimeOnly timeOfDay, int intervalDays)
        => (Frequency, LocalTimeOfDay, IntervalDays) = (frequency, timeOfDay, intervalDays);

    public static RecurrenceRule Daily(TimeOnly localTimeOfDay, int intervalDays = MinIntervalDays)
    {
        if (intervalDays is < MinIntervalDays or > MaxIntervalDays)
        {
            throw new InvalidRecurrenceException(
                $"El intervalo debe estar entre {MinIntervalDays} y {MaxIntervalDays} días.");
        }

        return new RecurrenceRule(Enums.RecurrenceFrequency.Daily, localTimeOfDay, intervalDays);
    }

    /// <summary>
    /// Calcula la próxima ocurrencia estrictamente posterior a <paramref name="afterUtc"/>
    /// expresada en UTC, usando la zona horaria indicada.
    /// </summary>
    public DateTimeOffset NextOccurrenceAfter(DateTimeOffset afterUtc, TimeZoneId zone)
    {
        var zoneInfo = zone.ToTimeZoneInfo();
        var localCurrentDate = TimeZoneInfo.ConvertTime(afterUtc, zoneInfo).DateTime.Date;

        var candidateDate = localCurrentDate;
        var candidateUtc = At(candidateDate).ToUtc(zoneInfo);

        if (candidateUtc <= afterUtc)
        {
            candidateDate = candidateDate.AddDays(IntervalDays);
        }

        var guard = 0;
        while (guard++ < 500)
        {
            candidateUtc = At(candidateDate).ToUtc(zoneInfo);
            if (candidateUtc > afterUtc)
            {
                return candidateUtc;
            }

            candidateDate = candidateDate.AddDays(IntervalDays);
        }

        throw new InvalidRecurrenceException("No fue posible calcular la próxima ocurrencia de la recurrencia.");

        DateTime At(DateTime date) => new(
            date.Year,
            date.Month,
            date.Day,
            LocalTimeOfDay.Hour,
            LocalTimeOfDay.Minute,
            LocalTimeOfDay.Second,
            DateTimeKind.Unspecified);
    }
}
