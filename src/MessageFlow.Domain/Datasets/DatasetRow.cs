using MessageFlow.Domain.Common;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Datasets;

/// <summary>
/// Fila de datos con valores dinámicos. Los valores se serializan como jsonb
/// (clave = nombre de columna normalizado). El tipado lo aporta DatasetColumn.DataType.
/// Las filas son millones potenciales: se acceden por consultas separadas, nunca
/// se cargan junto al agregado Dataset.
/// </summary>
public sealed class DatasetRow : Entity
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public Guid DatasetId { get; private set; }

    /// <summary>Posición de la fila en el archivo (1-based), para trazabilidad e importaciones reanudables.</summary>
    public long RowNumber { get; private set; }

    public IReadOnlyDictionary<string, string?> Values => _values;

    private DatasetRow()
    {
    }

    public DatasetRow(Guid datasetId, long rowNumber, IEnumerable<KeyValuePair<string, string?>> values)
    {
        if (datasetId == Guid.Empty)
        {
            throw new DomainException("La fila debe pertenecer a un dataset.");
        }

        if (rowNumber < 1)
        {
            throw new DomainException("El número de fila debe ser mayor o igual a 1.");
        }

        ArgumentNullException.ThrowIfNull(values);

        DatasetId = datasetId;
        RowNumber = rowNumber;
        _values = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
    }
}
