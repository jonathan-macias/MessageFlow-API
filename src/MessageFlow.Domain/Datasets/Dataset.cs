using MessageFlow.Domain.Common;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Datasets;

public sealed class Dataset : AuditableEntity
{
    public const int NameMaxLength = 200;

    private readonly List<DatasetColumn> _columns = [];

    /// <summary>UserId del propietario del Dataset (ownership, §34).</summary>
    public string OwnerId { get; private set; } = default!;

    public string Name { get; private set; } = default!;

    public string SourceFileName { get; private set; } = default!;

    /// <summary>
    /// Columna del dataset que contiene el número telefónico de destino (§30/§7):
    /// el usuario la elige al importar y puede cambiarla después. Se almacena por
    /// NOMBRE normalizado (la estructura de columnas es inmutable tras la carga).
    /// </summary>
    public string PhoneColumn { get; private set; } = default!;

    public string? StoragePath { get; private set; }

    public long RowCount { get; private set; }

    public IReadOnlyCollection<DatasetColumn> Columns => _columns.AsReadOnly();

    private Dataset()
    {
    }

    public static Dataset Create(string? name, string? sourceFileName, string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new Exceptions.DomainException("The dataset name is required.");
        }

        var trimmedName = name.Trim();

        if (trimmedName.Length > NameMaxLength)
        {
            throw new Exceptions.DomainException($"The dataset name cannot exceed {NameMaxLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(sourceFileName))
        {
            throw new InvalidDatasetFileException("The source file name is required.");
        }

        return new Dataset
        {
            OwnerId = ownerId,
            Name = trimmedName,
            SourceFileName = sourceFileName.Trim(),
        };
    }

    /// <summary>
    /// Normaliza el encabezado: recorta espacios, colapsa espacios internos y limita longitud.
    /// </summary>
    public static string NormalizeColumnName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            throw new InvalidDatasetFileException("Column headers cannot be empty.");
        }

        var normalized = string.Join(
            ' ',
            rawName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized.Length > DatasetColumn.NameMaxLength
            ? normalized[..DatasetColumn.NameMaxLength]
            : normalized;
    }

    public DatasetColumn AddColumn(string rawName, ColumnDataType dataType, int ordinal)
    {
        var name = NormalizeColumnName(rawName);

        if (_columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DuplicateColumnException(name);
        }

        if (_columns.Any(c => c.Ordinal == ordinal))
        {
            throw new Exceptions.DomainException($"A column already exists at position {ordinal} of the dataset.");
        }

        var column = new DatasetColumn(Id, name, dataType, ordinal);
        _columns.Add(column);
        return column;
    }

    /// <summary>
    /// Configura la columna telefónica del dataset. La comparación usa la misma
    /// estrategia del resto de columnas: nombre normalizado, insensible a
    /// mayúsculas/minúsculas. Debe corresponder a una columna existente; en caso
    /// contrario lanza <see cref="UnknownColumnException"/> sin modificar el valor.
    /// </summary>
    public void SetPhoneColumn(string? rawPhoneColumn)
    {
        if (string.IsNullOrWhiteSpace(rawPhoneColumn))
        {
            throw new InvalidDatasetFileException("The phone column is required.");
        }

        var normalized = NormalizeColumnName(rawPhoneColumn);

        // Siempre se almacena el nombre CANÓNICO registrado de la columna,
        // independientemente de las mayúsculas o espacios que envíe el cliente.
        var match = _columns.FirstOrDefault(c =>
            string.Equals(c.Name, normalized, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new UnknownColumnException(normalized);
        }

        PhoneColumn = match.Name;
    }

    public bool HasColumn(string name)
        => _columns.Any(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public DatasetColumn? FindColumn(Guid columnId)
        => _columns.FirstOrDefault(c => c.Id == columnId);

    public IReadOnlyList<ColumnDefinition> GetColumnDefinitions()
        => [.. _columns
            .OrderBy(c => c.Ordinal)
            .Select(c => new ColumnDefinition(c.Id, c.Name, c.DataType))];

    public void SetStoragePath(string storagePath)
    {
        StoragePath = storagePath;
    }

    public void SetRowCount(long rowCount)
    {
        RowCount = rowCount < 0 ? 0 : rowCount;
    }
}
