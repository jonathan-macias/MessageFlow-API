using MessageFlow.Domain.Enums;

namespace MessageFlow.Domain.Filters;

/// <summary>
/// Define qué operadores aplican según el tipo de columna y cuáles requieren valor.
/// Fuente única de verdad compartida por validadores (Application) y traductores (Infrastructure).
/// </summary>
public static class FilterOperatorPolicy
{
    private static readonly FilterOperator[] TextOperators =
    [
        FilterOperator.Equals,
        FilterOperator.NotEquals,
        FilterOperator.Contains,
        FilterOperator.StartsWith,
        FilterOperator.EndsWith,
        FilterOperator.IsNull,
        FilterOperator.IsNotNull,
    ];

    private static readonly FilterOperator[] NumberOperators =
    [
        FilterOperator.Equals,
        FilterOperator.NotEquals,
        FilterOperator.GreaterThan,
        FilterOperator.GreaterThanOrEqual,
        FilterOperator.LessThan,
        FilterOperator.LessThanOrEqual,
        FilterOperator.IsNull,
        FilterOperator.IsNotNull,
    ];

    private static readonly FilterOperator[] DateOperators =
    [
        FilterOperator.Equals,
        FilterOperator.Before,
        FilterOperator.After,
        FilterOperator.BeforeOrEqual,
        FilterOperator.AfterOrEqual,
        FilterOperator.IsNull,
        FilterOperator.IsNotNull,
    ];

    private static readonly FilterOperator[] BooleanOperators =
    [
        FilterOperator.Equals,
        FilterOperator.NotEquals,
        FilterOperator.IsNull,
        FilterOperator.IsNotNull,
    ];

    public static IReadOnlyList<FilterOperator> AllowedFor(ColumnDataType dataType) => dataType switch
    {
        ColumnDataType.Text => TextOperators,
        ColumnDataType.Number => NumberOperators,
        ColumnDataType.Date => DateOperators,
        ColumnDataType.Boolean => BooleanOperators,
        _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Tipo de columna desconocido."),
    };

    public static bool IsAllowed(ColumnDataType dataType, FilterOperator @operator)
        => AllowedFor(dataType).Contains(@operator);

    public static bool RequiresValue(FilterOperator @operator)
        => @operator is not (FilterOperator.IsNull or FilterOperator.IsNotNull);
}
