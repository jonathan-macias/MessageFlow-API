using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Filters;

/// <summary>
/// Motor de evaluación del árbol de filtros sobre los valores de una fila.
/// Se construye UNA vez validando estructura y catálogo, pre-parseando los literales
/// tipados; luego evalúa millones de filas sin re-validar ni re-parsear la condición.
/// Nunca genera SQL: los consumidores deciden dónde ejecutarlo (preview en streaming,
/// procesamiento background, etc.), garantizando semántica idéntica en todo el sistema.
///
/// Contrato: <paramref name="rowValues"/> debe usar comparación OrdinalIgnoreCase
/// (garantizado por DatasetRow.Values).
/// Celdas con valor no parseable al tipo de su columna hacen que la condición sea falsa
/// (la fila simplemente no coincide; nunca interrumpe un procesamiento masivo).
/// </summary>
public sealed class FilterMatcher
{
    private readonly FilterGroup? _root;
    private readonly IReadOnlyDictionary<Guid, ColumnDefinition> _catalog;

    public FilterMatcher(FilterGroup? root, IReadOnlyList<ColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        _root = root;
        _catalog = columns.ToDictionary(c => c.Id, c => c);

        if (root is not null)
        {
            FilterTreeValidator.Validate(root, columns);
        }
    }

    /// <summary>Árbol nulo (sin filtro): todas las filas coinciden.</summary>
    public bool Matches(IReadOnlyDictionary<string, string?> rowValues)
        => _root is null || Evaluate(_root, rowValues);

    private bool Evaluate(FilterNode node, IReadOnlyDictionary<string, string?> row) => node switch
    {
        FilterCondition condition => EvaluateCondition(condition, row),
        FilterGroup group => group.Composition == FilterCompositionOperator.And
            ? group.Conditions.All(child => Evaluate(child, row))
            : group.Conditions.Any(child => Evaluate(child, row)),
        _ => throw new InvalidFilterStructureException("Tipo de nodo de filtro desconocido."),
    };

    private bool EvaluateCondition(FilterCondition condition, IReadOnlyDictionary<string, string?> row)
    {
        if (!_catalog.TryGetValue(condition.ColumnId, out var column))
        {
            // Estructura validada en construcción; defensa adicional ante catálogos inconsistentes.
            return false;
        }

        var raw = row.TryGetValue(column.Name, out var value) ? value : null;

        switch (condition.Operator)
        {
            case FilterOperator.IsNull:
                return string.IsNullOrWhiteSpace(raw);
            case FilterOperator.IsNotNull:
                return !string.IsNullOrWhiteSpace(raw);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return column.DataType switch
        {
            ColumnDataType.Text => EvaluateText(condition.Operator, raw!.Trim(), condition.Value ?? string.Empty),
            ColumnDataType.Number => EvaluateNumber(condition.Operator, raw!.Trim(), condition.Value),
            ColumnDataType.Date => EvaluateDate(condition.Operator, raw!.Trim(), condition.Value),
            ColumnDataType.Boolean => EvaluateBoolean(condition.Operator, raw!.Trim(), condition.Value),
            _ => false,
        };
    }

    private static bool EvaluateText(FilterOperator op, string cell, string expected) => op switch
    {
        FilterOperator.Equals => string.Equals(cell, expected, StringComparison.OrdinalIgnoreCase),
        FilterOperator.NotEquals => !string.Equals(cell, expected, StringComparison.OrdinalIgnoreCase),
        FilterOperator.Contains => cell.Contains(expected, StringComparison.OrdinalIgnoreCase),
        FilterOperator.StartsWith => cell.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
        FilterOperator.EndsWith => cell.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool EvaluateNumber(FilterOperator op, string cell, string? expectedRaw)
    {
        if (!FilterValueCoercion.TryParseNumber(cell, out var cellValue)
            || !FilterValueCoercion.TryParseNumber(expectedRaw, out var expected))
        {
            return false;
        }

        return op switch
        {
            FilterOperator.Equals => cellValue == expected,
            FilterOperator.NotEquals => cellValue != expected,
            FilterOperator.GreaterThan => cellValue > expected,
            FilterOperator.GreaterThanOrEqual => cellValue >= expected,
            FilterOperator.LessThan => cellValue < expected,
            FilterOperator.LessThanOrEqual => cellValue <= expected,
            _ => false,
        };
    }

    private static bool EvaluateDate(FilterOperator op, string cell, string? expectedRaw)
    {
        if (!FilterValueCoercion.TryParseDate(cell, out var cellValue)
            || !FilterValueCoercion.TryParseDate(expectedRaw, out var expected))
        {
            return false;
        }

        return op switch
        {
            FilterOperator.Equals => cellValue == expected,
            FilterOperator.Before => cellValue < expected,
            FilterOperator.After => cellValue > expected,
            FilterOperator.BeforeOrEqual => cellValue <= expected,
            FilterOperator.AfterOrEqual => cellValue >= expected,
            _ => false,
        };
    }

    private static bool EvaluateBoolean(FilterOperator op, string cell, string? expectedRaw)
    {
        if (!FilterValueCoercion.TryParseBoolean(cell, out var cellValue)
            || !FilterValueCoercion.TryParseBoolean(expectedRaw, out var expected))
        {
            return false;
        }

        return op switch
        {
            FilterOperator.Equals => cellValue == expected,
            FilterOperator.NotEquals => cellValue != expected,
            _ => false,
        };
    }
}
