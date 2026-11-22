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
            throw new UnsupportedTimeZoneException("The time zone is required.");
        }

        var trimmed = value.Trim();

        if (trimmed.Contains(' '))
        {
            throw new UnsupportedTimeZoneException(
                $"The time zone '{trimmed}' is not a valid IANA identifier. Use, for example, 'America/Bogota'.");
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(trimmed);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new UnsupportedTimeZoneException($"Unknown time zone: '{trimmed}'.");
        }
        catch (InvalidTimeZoneException)
        {
            throw new UnsupportedTimeZoneException($"Invalid time zone: '{trimmed}'.");
        }

        return new TimeZoneId(trimmed);
    }

    /// <summary>Construye el VO sin revalidar (para valores ya persistidos y verificados).</summary>
    public static TimeZoneId FromStored(string value) => new(value);

    public TimeZoneInfo ToTimeZoneInfo() => TimeZoneInfo.FindSystemTimeZoneById(Value);

    public override string ToString() => Value;
}
