using System.Globalization;
using MessageFlow.Domain.AI;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;

namespace MessageFlow.Application.AI;

/// <summary>
/// Evaluación de filtros estructurados sobre los valores de una fila.
/// Compartida por la consulta estructurada (<see cref="DatasetQueryExecutor"/>) y la
/// búsqueda semántica híbrida, para que ambos caminos apliquen exactamente las mismas
/// reglas de comparación.
///
/// Los valores viven en <c>dataset_rows.values</c> como <c>jsonb</c> (no como columnas
/// tipadas), así que el filtrado ocurre en memoria: la base de datos acota primero por
/// similitud vectorial y sobre ese conjunto acotado se evalúan los filtros.
/// </summary>
public static class DatasetRowFilterEvaluator
{
    /// <summary>Resuelve el catálogo de columnas por nombre normalizado.</summary>
    public static Dictionary<string, ColumnDefinition> BuildColumnIndex(IEnumerable<ColumnDefinition> columns)
        => columns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);

    /// <summary>Devuelve <c>true</c> si la fila cumple todos los filtros (o si no hay filtros).</summary>
    public static bool Matches(
        IReadOnlyDictionary<string, string?> rowValues,
        IReadOnlyList<DatasetQueryFilter> filters,
        IReadOnlyDictionary<string, ColumnDefinition> columnIndex)
    {
        if (filters.Count == 0)
        {
            return true;
        }

        // Una columna inexistente hace que la fila no cumpla: falla cerrado.
        return filters.All(filter => Matches(rowValues, filter, columnIndex));
    }

    private static bool Matches(
        IReadOnlyDictionary<string, string?> rowValues,
        DatasetQueryFilter filter,
        IReadOnlyDictionary<string, ColumnDefinition> columnIndex)
    {
        if (!columnIndex.TryGetValue(filter.Column, out var column))
        {
            return false;
        }

        rowValues.TryGetValue(filter.Column, out var cellValue);

        return filter.Operator switch
        {
            DatasetQueryOperator.Equals => string.Equals(cellValue, filter.Value, StringComparison.OrdinalIgnoreCase),
            DatasetQueryOperator.NotEquals => !string.Equals(cellValue, filter.Value, StringComparison.OrdinalIgnoreCase),
            DatasetQueryOperator.Contains => cellValue?.Contains(filter.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase) ?? false,
            DatasetQueryOperator.GreaterThan => CompareValues(cellValue, filter.Value, column.DataType) > 0,
            DatasetQueryOperator.GreaterThanOrEqual => CompareValues(cellValue, filter.Value, column.DataType) >= 0,
            DatasetQueryOperator.LessThan => CompareValues(cellValue, filter.Value, column.DataType) < 0,
            DatasetQueryOperator.LessThanOrEqual => CompareValues(cellValue, filter.Value, column.DataType) <= 0,
            DatasetQueryOperator.IsNull => string.IsNullOrWhiteSpace(cellValue),
            DatasetQueryOperator.IsNotNull => !string.IsNullOrWhiteSpace(cellValue),
            _ => false,
        };
    }

    /// <summary>Compara según el tipo declarado de la columna, con respaldo textual.</summary>
    public static int CompareValues(string? cellValue, string? expectedValue, ColumnDataType dataType)
    {
        if (string.IsNullOrWhiteSpace(cellValue) || string.IsNullOrWhiteSpace(expectedValue))
        {
            return string.IsNullOrWhiteSpace(cellValue)
                ? (string.IsNullOrWhiteSpace(expectedValue) ? 0 : -1)
                : 1;
        }

        return dataType switch
        {
            ColumnDataType.Number => CompareNumbers(cellValue, expectedValue),
            ColumnDataType.Date => CompareDates(cellValue, expectedValue),
            _ => string.Compare(cellValue, expectedValue, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static int CompareNumbers(string cell, string expected)
    {
        if (double.TryParse(cell, NumberStyles.Any, CultureInfo.InvariantCulture, out var cellNum) &&
            double.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var expectedNum))
        {
            return cellNum.CompareTo(expectedNum);
        }

        return string.Compare(cell, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareDates(string cell, string expected)
    {
        if (DateTime.TryParse(cell, CultureInfo.InvariantCulture, DateTimeStyles.None, out var cellDate) &&
            DateTime.TryParse(expected, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expectedDate))
        {
            return cellDate.CompareTo(expectedDate);
        }

        return string.Compare(cell, expected, StringComparison.OrdinalIgnoreCase);
    }
}
