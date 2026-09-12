using System.Globalization;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Filters;

namespace MessageFlow.Application.Datasets;

/// <summary>
/// Normaliza los valores crudos de las celdas de Excel a texto canónico antes de
/// cualquier inferencia o almacenamiento. Las fechas con hora cero se representan
/// solo como fecha (caso dominante en datasets de contactos).
/// </summary>
public static class ExcelCellFormatter
{
    public static string? Format(object? cellValue)
    {
        switch (cellValue)
        {
            case null or DBNull:
                return null;
            case string text:
                return text.Trim().Length == 0 ? null : text;
            case DateTime dateTime:
                return dateTime.TimeOfDay == TimeSpan.Zero
                    ? dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case bool flag:
                return flag ? "true" : "false";
            default:
                var converted = Convert.ToString(cellValue, CultureInfo.InvariantCulture)?.Trim();
                return string.IsNullOrEmpty(converted) ? null : converted;
        }
    }
}

/// <summary>
/// Infiere el tipo de cada columna muestreando las primeras N filas del archivo.
/// Regla: si TODAS las muestras no nulas del campo cumplen un tipo, ese es su tipo;
/// columnas sin datos quedan como texto (el caso más permisivo para filtros).
/// </summary>
public static class ColumnTypeInference
{
    /// <param name="sampleRows">Filas ya normalizadas a texto por <see cref="ExcelCellFormatter"/>.</param>
    /// <returns>Tipos alineados por ordinal con las columnas del encabezado.</returns>
    public static IReadOnlyList<ColumnDataType> Infer(
        IReadOnlyList<IReadOnlyList<string?>> sampleRows,
        int columnCount)
    {
        var result = new Domain.Enums.ColumnDataType[columnCount];
        Array.Fill(result, Domain.Enums.ColumnDataType.Text);

        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            var isDate = true;
            var isNumber = true;
            var isBoolean = true;
            var sawValue = false;

            foreach (var row in sampleRows)
            {
                var value = columnIndex < row.Count ? row[columnIndex] : null;

                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                sawValue = true;
                isDate &= FilterValueCoercion.TryParseDate(value, out _);
                isNumber &= FilterValueCoercion.TryParseNumber(value, out _);
                isBoolean &= FilterValueCoercion.TryParseBoolean(value, out _);

                if (!isDate && !isNumber && !isBoolean)
                {
                    break;
                }
            }

            result[columnIndex] = !sawValue
                ? ColumnDataType.Text
                : isDate ? ColumnDataType.Date
                : isNumber ? ColumnDataType.Number
                : isBoolean ? ColumnDataType.Boolean
                : ColumnDataType.Text;
        }

        return result;
    }

    /// <summary>Convierte el valor textual al formato canónico del tipo inferido; si no aplica, conserva el texto.</summary>
    public static string? Canonicalize(string? rawValue, ColumnDataType dataType)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var value = rawValue.Trim();

        return dataType switch
        {
            ColumnDataType.Date when FilterValueCoercion.TryParseDate(value, out var date)
                => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ColumnDataType.Number when FilterValueCoercion.TryParseNumber(value, out var number)
                => number.ToString(CultureInfo.InvariantCulture),
            ColumnDataType.Boolean when FilterValueCoercion.TryParseBoolean(value, out var flag)
                => flag ? "true" : "false",
            _ => value,
        };
    }
}
