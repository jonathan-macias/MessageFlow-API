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
            throw new InvalidFilterStructureException("The filter condition must specify a valid column.");
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
            throw new InvalidFilterStructureException("A filter group must contain at least one condition.");
        }

        if (list.Any(c => c is null))
        {
            throw new InvalidFilterStructureException("The filter group contains null conditions.");
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
                $"The maximum allowed depth ({MaxDepth}) of the filter tree was exceeded.");
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
                    throw new InvalidFilterStructureException("Unknown filter node type.");
            }
        }
    }
}
