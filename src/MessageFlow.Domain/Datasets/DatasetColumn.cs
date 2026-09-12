using MessageFlow.Domain.Common;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Datasets;

public sealed class DatasetColumn : Entity
{
    public const int NameMaxLength = 200;

    public Guid DatasetId { get; private set; }

    /// <summary>Nombre normalizado (ver Dataset.NormalizeColumnName). Único por dataset, case-insensitive.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>Tipo inferido durante la importación (muestra de datos). Gobierna operadores de filtro válidos.</summary>
    public ColumnDataType DataType { get; private set; }

    /// <summary>Posición de la columna en el archivo original (0-based).</summary>
    public int Ordinal { get; private set; }

    private DatasetColumn()
    {
    }

    internal DatasetColumn(Guid datasetId, string name, ColumnDataType dataType, int ordinal)
        : this()
    {
        if (datasetId == Guid.Empty)
        {
            throw new DomainException("La columna debe pertenecer a un dataset.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDatasetFileException("El nombre de la columna no puede estar vacío.");
        }

        if (ordinal < 0)
        {
            throw new DomainException("La posición de la columna no puede ser negativa.");
        }

        DatasetId = datasetId;
        Name = name;
        DataType = dataType;
        Ordinal = ordinal;
    }
}
