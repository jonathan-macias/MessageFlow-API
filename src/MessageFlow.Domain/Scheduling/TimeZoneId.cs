using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Scheduling;

/// <summary>
/// Value Object que encapsula un identificador de zona horaria IANA (p. ej. "America/Bogota").
/// Se valida al crearse contra TimeZoneInfo (soporta IDs IANA cross-platform desde .NET 8+).
/// </summary>
public readonly record struct TimeZoneId
{
    public string Value { get; }

    private TimeZoneId(string value) => Value = value;

    public static TimeZoneId Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UnsupportedTimeZoneException("La zona horaria es obligatoria.");
        }

        var trimmed = value.Trim();

        if (trimmed.Contains(' '))
        {
            throw new UnsupportedTimeZoneException(
                $"La zona horaria '{trimmed}' no es un identificador IANA válido. Use por ejemplo 'America/Bogota'.");
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(trimmed);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new UnsupportedTimeZoneException($"Zona horaria desconocida: '{trimmed}'.");
        }
        catch (InvalidTimeZoneException)
        {
            throw new UnsupportedTimeZoneException($"Zona horaria inválida: '{trimmed}'.");
        }

        return new TimeZoneId(trimmed);
    }

    /// <summary>Construye el VO sin revalidar (para valores ya persistidos y verificados).</summary>
    public static TimeZoneId FromStored(string value) => new(value);

    public TimeZoneInfo ToTimeZoneInfo() => TimeZoneInfo.FindSystemTimeZoneById(Value);

    public override string ToString() => Value;
}
