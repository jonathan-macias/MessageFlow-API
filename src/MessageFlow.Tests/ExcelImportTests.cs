using MessageFlow.Application.Datasets;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Infrastructure.Persistence.Files;
using MiniExcelLibs;

namespace MessageFlow.Tests;

public class ExcelImportTests
{
    /// <summary>
    /// Construye un xlsx cuyo layout físico coincide con un archivo real del usuario:
    /// primera fila = encabezados, siguientes filas = datos.
    /// Al guardar diccionarios, MiniExcel SIEMPRE emite las claves como primera fila;
    /// por eso la fila de encabezados deseada se usa como claves y se omiten del
    /// cuerpo. MiniExcel omite las claves vacías, así que un encabezado en blanco se
    /// representa con espacios (el formateador los normaliza a null y el lector lo rechaza).
    /// </summary>
    private static MemoryStream CreateWorkbook(params object?[][] physicalRows)
    {
        var columnCount = physicalRows.Max(r => r.Length);

        var headerKeys = Enumerable.Range(0, columnCount)
            .Select(i => physicalRows[0].ElementAtOrDefault(i)?.ToString() is { Length: > 0 } header
                ? header
                : new string(' ', i + 1))
            .ToArray();

        var entries = new List<Dictionary<string, object?>>();

        foreach (var row in physicalRows.Skip(1))
        {
            var entry = new Dictionary<string, object?>();

            for (var i = 0; i < Math.Min(row.Length, columnCount); i++)
            {
                entry[headerKeys[i]] = row[i];
            }

            entries.Add(entry);
        }

        var stream = new MemoryStream();
        stream.SaveAs(entries);
        stream.Position = 0;
        return stream;
    }

    [ExcelRuntimeFact]
    public void Reader_extracts_headers_and_rows_in_order()
    {
        using var stream = CreateWorkbook(
            new object?[] { "Nombre", "Correo", "FechaNacimiento" },
            new object?[] { "Juan", "juan@gmail.com", "1990-01-08" },
            new object?[] { "Pedro", DBNull.Value, "1988-05-12" });

        using var reader = new MiniExcelReaderFactory().Open(stream);

        var headers = reader.ReadHeaders();
        var rows = reader.ReadDataRows().ToList();

        Assert.Equal(["Nombre", "Correo", "FechaNacimiento"], headers);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Juan", rows[0][0]);
        Assert.Equal("juan@gmail.com", rows[0][1]);
        Assert.Equal("1990-01-08", rows[0][2]);
        Assert.Null(rows[1]![1]);
        Assert.Equal("1988-05-12", rows[1][2]);
    }

    [Fact]
    public void Inference_assigns_types_by_column_sample()
    {
        IReadOnlyList<IReadOnlyList<string?>> rows =
        [
            ["1990-01-08", "25", "true", "PRINCIPAL"],
            ["1988-05-12", "30.5", "false", "SECUNDARIO"],
        ];

        var types = ColumnTypeInference.Infer(rows, columnCount: 4);

        Assert.Equal([
            ColumnDataType.Date,
            ColumnDataType.Number,
            ColumnDataType.Boolean,
            ColumnDataType.Text,
        ], types);
    }

    [Fact]
    public void Canonicalize_normalizes_values_to_storage_format()
    {
        // Formato invariante (mismo criterio que FilterValueCoercion en filtros).
        Assert.Equal("1990-01-08", ColumnTypeInference.Canonicalize("08-01-1990", ColumnDataType.Date));
        Assert.Equal("30.5", ColumnTypeInference.Canonicalize(" 30.5 ", ColumnDataType.Number));
        Assert.Equal("1234.50", ColumnTypeInference.Canonicalize("1,234.50", ColumnDataType.Number));
        Assert.Equal("true", ColumnTypeInference.Canonicalize("TRUE", ColumnDataType.Boolean));
        Assert.Null(ColumnTypeInference.Canonicalize("   ", ColumnDataType.Text));
    }

    [ExcelRuntimeFact]
    public void Empty_headers_are_rejected()
    {
        // La celda B del encabezado queda vacía (la columna existe por la segunda entrada).
        using var stream = CreateWorkbook(
            new object?[] { "Nombre", DBNull.Value },
            new object?[] { "Juan", "x" });

        using var reader = new MiniExcelReaderFactory().Open(stream);

        // El lector DETECTA el encabezado en blanco normalizándolo a null;
        // el rechazo ocurre en la normalización del dominio.
        var headers = reader.ReadHeaders();

        Assert.Equal(2, headers.Count);
        Assert.Null(headers[1]);
        Assert.Throws<InvalidDatasetFileException>(() => Dataset.NormalizeColumnName(headers[1]!));
    }
}
