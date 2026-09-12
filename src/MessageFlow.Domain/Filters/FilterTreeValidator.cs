using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Filters;

/// <summary>
/// Valida un árbol de filtros contra el catálogo de columnas de un dataset:
/// existencia de columnas, operadores soportados por tipo y coerción del valor.
/// Único punto de validación, compartido por Flow y por pruebas ad-hoc (preview).
/// </summary>
public static class FilterTreeValidator
{
    public static void Validate(FilterGroup? root, IReadOnlyList<ColumnDefinition> columns)
    {
        if (root is null)
        {
            return;
        }

        var catalog = columns.ToDictionary(c => c.Id, c => c);

        foreach (var condition in EnumerateConditions(root))
        {
            if (!catalog.TryGetValue(condition.ColumnId, out var column))
            {
                throw new DomainException(
                    $"La condición de filtro referencia una columna inexistente en el dataset (columna '{condition.ColumnId}').");
            }

            if (!FilterOperatorPolicy.IsAllowed(column.DataType, condition.Operator))
            {
                throw new UnsupportedFilterOperatorException(column.DataType, condition.Operator);
            }

            FilterValueCoercion.Coerce(column.DataType, condition.Operator, condition.Value);
        }
    }

    private static IEnumerable<FilterCondition> EnumerateConditions(FilterNode node) => node switch
    {
        FilterCondition condition => [condition],
        FilterGroup group => group.Conditions.SelectMany(EnumerateConditions),
        _ => [],
    };
}
