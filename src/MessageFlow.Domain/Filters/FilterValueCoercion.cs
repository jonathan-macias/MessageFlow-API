using System.Globalization;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Filters;

/// <summary>
/// Convierte el valor crudo (string) de una condición al tipo de la columna.
/// Se ejecuta después de validar que la columna existe y el operador es compatible,
/// garantizando que los traductores solo reciben valores tipados y seguros.
/// </summary>
public static class FilterValueCoercion
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd",
        "yyyy/MM/dd",
        "dd-MM-yyyy",
        "MM/dd/yyyy",
    ];

    public static object? Coerce(ColumnDataType dataType, FilterOperator @operator, string? rawValue)
    {
        if (!FilterOperatorPolicy.RequiresValue(@operator))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            throw new InvalidFilterValueException("La condición de filtro requiere un valor.");
        }

        var trimmed = rawValue.Trim();

        return dataType switch
        {
            ColumnDataType.Text => rawValue,
            ColumnDataType.Number => ParseNumber(trimmed),
            ColumnDataType.Date => ParseDate(trimmed),
            ColumnDataType.Boolean => ParseBoolean(trimmed),
            _ => throw new InvalidFilterValueException($"Tipo de columna no soportado para filtros: '{dataType}'."),
        };
    }

    /// <summary>Único set de formatos de fecha aceptados (filtros, importación e inferencia).</summary>
    public static bool TryParseDate(string? value, out DateOnly date)
        => DateOnly.TryParseExact(
                value,
                DateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);

    public static bool TryParseNumber(string? value, out decimal number)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out number);

    public static bool TryParseBoolean(string? value, out bool flag)
        => bool.TryParse(value, out flag);

    private static decimal ParseNumber(string value)
        => TryParseNumber(value, out var number)
            ? number
            : throw new InvalidFilterValueException($"'{value}' no es un número válido.");

    private static DateOnly ParseDate(string value)
        => TryParseDate(value, out var date)
            ? date
            : throw new InvalidFilterValueException(
                $"'{value}' no es una fecha válida. Formatos aceptados: {string.Join(", ", DateFormats)}.");

    private static bool ParseBoolean(string value)
        => TryParseBoolean(value, out var flag)
            ? flag
            : throw new InvalidFilterValueException($"'{value}' no es un valor booleano válido (true/false).");
}
