namespace MessageFlow.Domain.Exceptions;

public sealed class DuplicateColumnException(string columnName)
    : DomainException($"El dataset ya contiene una columna llamada '{columnName}'.")
{
    public string ColumnName { get; } = columnName;
}

/// <summary>La columna indicada no existe entre las columnas del dataset.</summary>
public sealed class UnknownColumnException(string columnName)
    : DomainException($"La columna '{columnName}' no existe en el dataset.")
{
    public string ColumnName { get; } = columnName;
}

public sealed class InvalidDatasetFileException(string message)
    : DomainException(message)
{
}
