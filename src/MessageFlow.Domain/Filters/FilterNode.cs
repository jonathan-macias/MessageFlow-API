using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Filters;

/// <summary>
/// Árbol de filtros: condiciones simples o grupos (AND/OR) combinados.
/// Se construye siempre vía <see cref="FilterGroup.Create"/> para garantizar estructura válida.
/// </summary>
public abstract record FilterNode
{
}

public sealed record FilterCondition : FilterNode
{
    public Guid ColumnId { get; }

    public FilterOperator Operator { get; }

    /// <summary>Valor crudo enviado por el cliente; la coerción tipada ocurre al validar contra el catálogo.</summary>
    public string? Value { get; }

    public FilterCondition(Guid columnId, FilterOperator @operator, string? value)
    {
        if (columnId == Guid.Empty)
        {
            throw new InvalidFilterStructureException("La condición de filtro debe indicar una columna válida.");
        }

        ColumnId = columnId;
        Operator = @operator;
        Value = FilterOperatorPolicy.RequiresValue(@operator) ? value : null;
    }
}

public sealed record FilterGroup : FilterNode
{
    public const int MaxDepth = 10;

    public FilterCompositionOperator Composition { get; }

    public IReadOnlyList<FilterNode> Conditions { get; }

    private FilterGroup(FilterCompositionOperator composition, IReadOnlyList<FilterNode> conditions)
        => (Composition, Conditions) = (composition, conditions);

    public static FilterGroup Create(FilterCompositionOperator composition, IEnumerable<FilterNode> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);

        var list = conditions.ToList();

        if (list.Count == 0)
        {
            throw new InvalidFilterStructureException("Un grupo de filtros debe contener al menos una condición.");
        }

        if (list.Any(c => c is null))
        {
            throw new InvalidFilterStructureException("El grupo de filtros contiene condiciones nulas.");
        }

        var group = new FilterGroup(composition, list);
        group.Validate(depth: 1);
        return group;
    }

    private void Validate(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidFilterStructureException(
                $"Se excedió la profundidad máxima permitida ({MaxDepth}) en el árbol de filtros.");
        }

        foreach (var condition in Conditions)
        {
            switch (condition)
            {
                case FilterGroup nested:
                    nested.Validate(depth + 1);
                    break;
                case FilterCondition:
                    break;
                default:
                    throw new InvalidFilterStructureException("Tipo de nodo de filtro desconocido.");
            }
        }
    }
}
