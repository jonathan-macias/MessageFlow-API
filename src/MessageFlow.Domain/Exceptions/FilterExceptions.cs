using MessageFlow.Domain.Enums;

namespace MessageFlow.Domain.Exceptions;

public sealed class InvalidFilterStructureException(string message)
    : DomainException(message)
{
}

public sealed class InvalidFilterValueException(string message)
    : DomainException(message)
{
}

public sealed class UnsupportedFilterOperatorException(ColumnDataType dataType, Filters.FilterOperator @operator)
    : DomainException($"El operador '{@operator}' no está soportado para columnas de tipo '{dataType}'.")
{
    public ColumnDataType DataType { get; } = dataType;

    public Filters.FilterOperator Operator { get; } = @operator;
}
