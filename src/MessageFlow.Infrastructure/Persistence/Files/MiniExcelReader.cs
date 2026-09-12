using MessageFlow.Application.Abstractions.Files;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Application.Datasets;
using MiniExcelLibs;

namespace MessageFlow.Infrastructure.Persistence.Files;

/// <summary>
/// Lector de Excel en streaming con MiniExcel. Se lee SIN fila de encabezado
/// (useHeaderRow: false) para detectar explícitamente encabezados vacíos o duplicados.
/// </summary>
internal sealed class MiniExcelReader(Stream stream) : IExcelReader
{
    private readonly IEnumerable<IDictionary<string, object?>> _rows =
        stream.Query(useHeaderRow: false).Cast<IDictionary<string, object?>>();
    private IEnumerator<IDictionary<string, object?>>? _enumerator;
    private int _columnCount;

    public IReadOnlyList<string?> ReadHeaders()
    {
        var enumerator = GetEnumerator();

        if (!enumerator.MoveNext())
        {
            throw new InvalidDatasetFileException("El archivo no contiene filas.");
        }

        var firstRow = enumerator.Current;
        var columnKeys = OrderColumnKeys(firstRow.Keys);
        _columnCount = columnKeys.Count;

        return [.. columnKeys.Select(key => ExcelCellFormatter.Format(firstRow.TryGetValue(key, out var value) ? value : null))];
    }

    public IEnumerable<IReadOnlyList<string?>> ReadDataRows()
    {
        var enumerator = GetEnumerator();

        while (enumerator.MoveNext())
        {
            yield return MaterializeRow(enumerator.Current);
        }
    }

    public void Dispose() => _enumerator?.Dispose();

    private IEnumerator<IDictionary<string, object?>> GetEnumerator()
        => _enumerator ??= _rows.GetEnumerator();

    private string?[] MaterializeRow(IDictionary<string, object?> row)
    {
        var cells = new string?[_columnCount];

        for (var index = 0; index < _columnCount; index++)
        {
            cells[index] = row.TryGetValue(ColumnName(index), out var value)
                ? ExcelCellFormatter.Format(value)
                : null;
        }

        return cells;
    }

    /// <summary>Ordena claves tipo "A", "B", ..., "AA" por longitud y luego lexicográfico (orden real de hoja).</summary>
    private static IReadOnlyList<string> OrderColumnKeys(IEnumerable<string> keys)
        => [.. keys.OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal)];

    /// <summary>Índice base-cero a letra de columna estilo Excel: 0→A, 25→Z, 26→AA.</summary>
    internal static string ColumnName(int zeroBasedIndex)
    {
        checked
        {
            if (zeroBasedIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(zeroBasedIndex));
            }

            var name = string.Empty;
            var remaining = zeroBasedIndex;

            do
            {
                name = (char)('A' + remaining % 26) + name;
                remaining = remaining / 26 - 1;
            }
            while (remaining >= 0);

            return name;
        }
    }
}

internal sealed class MiniExcelReaderFactory : IExcelReaderFactory
{
    public IExcelReader Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidDatasetFileException("El stream del archivo debe ser legible y buscable.");
        }

        stream.Position = 0;
        return new MiniExcelReader(stream);
    }
}
