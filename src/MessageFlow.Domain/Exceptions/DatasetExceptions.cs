namespace MessageFlow.Domain.Exceptions;

public sealed class DuplicateColumnException(string columnName)
    : DomainException($"The dataset already contains a column named '{columnName}'.")
{
    public string ColumnName { get; } = columnName;
}

/// <summary>La columna indicada no existe entre las columnas del dataset.</summary>
public sealed class UnknownColumnException(string columnName)
    : DomainException($"The column '{columnName}' does not exist in the dataset.")
{
    public string ColumnName { get; } = columnName;
}

public sealed class InvalidDatasetFileException(string message)
    : DomainException(message)
{
}
