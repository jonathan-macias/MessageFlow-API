namespace MessageFlow.Domain.Scheduling;

internal static class TimeZoneConversion
{
    /// <summary>
    /// Convierte una hora local "wall clock" (especificada por el usuario en su zona horaria) a UTC.
    /// Política para horas inexistentes por DST (hueco de primavera): avanza 1 hora.
    /// Para horas ambiguas (otoño), .NET resuelve al offset estándar.
    /// </summary>
    public static DateTimeOffset ToUtc(this DateTime localWallTime, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(localWallTime, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
