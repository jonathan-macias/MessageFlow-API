using System.Globalization;
using System.Text;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;

namespace MessageFlow.Application.AI;

/// <summary>
/// Construye el texto que se envía al modelo de embeddings para una fila.
///
/// Solo se indexan columnas de tipo <see cref="ColumnDataType.Text"/>: los números
/// y las fechas se embeddingan como texto y degradan la calidad de las búsquedas
/// numéricas, además de que el endpoint de consulta estructurada los resuelve mejor
/// y de forma exacta. Esta separación es deliberada: modo semántico para significado,
/// modo estructurado para agregaciones.
/// </summary>
public static class DatasetRowEmbeddingTextBuilder
{
    public const string EmptyContent = "";

    /// <summary>
    /// Devuelve el texto indexable de la fila, o <see cref="EmptyContent"/> si la fila
    /// no tiene ninguna columna de texto con contenido.
    /// </summary>
    public static string Build(
        DatasetRow row,
        IReadOnlyList<ColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(columns);

        var builder = new StringBuilder();

        foreach (var column in columns)
        {
            if (column.DataType != ColumnDataType.Text)
            {
                continue;
            }

            if (!row.Values.TryGetValue(column.Name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(" | ");
            }

            builder.Append(CultureInfo.InvariantCulture, $"{column.Name}: {CollapseWhitespace(value)}");
        }

        return Truncate(builder.ToString());
    }

    /// <summary>
    /// Indica si el dataset tiene al menos una columna indexable. Si no, el indexado
    /// no tiene sentido y hay que avisarlo en lugar de generar embeddings vacíos.
    /// </summary>
    public static bool HasIndexableColumns(IReadOnlyList<ColumnDefinition> columns)
        => columns.Any(c => c.DataType == ColumnDataType.Text);

    private static string CollapseWhitespace(string value)
    {
        var segments = value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return string.Join(' ', segments);
    }

    private static string Truncate(string content)
    {
        if (content.Length <= DatasetRowEmbedding.ContentMaxLength)
        {
            return content;
        }

        // Corta en el último separador para no dejar una palabra partida a mitad.
        var cut = content.LastIndexOf(' ', DatasetRowEmbedding.ContentMaxLength);
        return content[..(cut > 0 ? cut : DatasetRowEmbedding.ContentMaxLength)];
    }
}
