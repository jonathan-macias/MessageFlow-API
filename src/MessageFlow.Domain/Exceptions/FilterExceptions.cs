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
    : DomainException($"The operator '{@operator}' is not supported for columns of type '{dataType}'.")
{
    public ColumnDataType DataType { get; } = dataType;

    public Filters.FilterOperator Operator { get; } = @operator;
}
