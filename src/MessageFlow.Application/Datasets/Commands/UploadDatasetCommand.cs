using FluentValidation;
using MessageFlow.Application.Abstractions;
using MessageFlow.Application.Abstractions.Files;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Application.Datasets.Commands;

/// <summary>
/// Importación de un archivo Excel como Dataset (§8): valida el archivo, lee encabezados,
/// registra columnas con tipos inferidos e inserta las filas por lotes (streaming).
/// Requiere la columna telefónica: el usuario indica qué columna del archivo contiene
/// los números de destino; debe existir exactamente entre los encabezados.
/// </summary>
public sealed record UploadDatasetCommand(
    string Name,
    string SourceFileName,
    long ContentLength,
    Stream Content,
    string PhoneColumn) : ICommand<Guid>;

public sealed class UploadDatasetCommandValidator : AbstractValidator<UploadDatasetCommand>
{
    public UploadDatasetCommandValidator(DatasetImportOptions options)
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("El nombre del dataset es obligatorio.")
            .MaximumLength(Dataset.NameMaxLength);

        RuleFor(x => x.SourceFileName)
            .NotEmpty().WithMessage("El nombre del archivo es obligatorio.")
            .Must(f => f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Solo se aceptan archivos Excel (.xlsx).");

        RuleFor(x => x.ContentLength)
            .GreaterThan(0).WithMessage("El archivo está vacío.")
            .LessThanOrEqualTo(options.MaxFileSizeBytes)
            .WithMessage($"El archivo supera el tamaño máximo permitido ({options.MaxFileSizeBytes / (1024 * 1024)} MB).");

        RuleFor(x => x.PhoneColumn)
            .NotEmpty().WithMessage("La columna telefónica (phoneColumn) es obligatoria.");
    }
}

public sealed class UploadDatasetCommandHandler(
    IExcelReaderFactory readerFactory,
    IDatasetFileStorage fileStorage,
    IDatasetRepository datasetRepository,
    IUnitOfWork unitOfWork,
    DatasetImportOptions options,
    ICurrentUser currentUser) : ICommandHandler<UploadDatasetCommand, Guid>
{
    public async Task<Guid> HandleAsync(UploadDatasetCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Content.CanSeek && command.Content.Length > options.MaxFileSizeBytes)
        {
            throw new InvalidDatasetFileException(
                $"El archivo supera el tamaño máximo permitido ({options.MaxFileSizeBytes / (1024 * 1024)} MB).");
        }

        // El binario original se persiste primero: la lectura de Excel ocurre sobre
        // el archivo almacenado (stream buscable y trazable vía StoragePath).
        var storagePath = await fileStorage.SaveAsync(command.Content, command.SourceFileName, cancellationToken);

        try
        {
            return await ImportAsync(command, storagePath, cancellationToken);
        }
        catch
        {
            // La importación fallida no debe dejar archivos huérfanos.
            TryDeleteQuietly(storagePath);
            throw;
        }
    }

    private async Task<Guid> ImportAsync(UploadDatasetCommand command, string storagePath, CancellationToken cancellationToken)
    {
        using var stream = fileStorage.OpenRead(storagePath);
        using var reader = readerFactory.Open(stream);

        var headers = ReadAndValidateHeaders(reader);
        var columnCount = headers.Count;

        // Muestreo acotado para inferir tipos antes de crear columnas.
        var sampleRows = BufferSampleRows(reader);
        var dataTypes = ColumnTypeInference.Infer(sampleRows, columnCount);

        var dataset = Dataset.Create(command.Name, command.SourceFileName, currentUser.UserId!);

        for (var ordinal = 0; ordinal < columnCount; ordinal++)
        {
            dataset.AddColumn(headers[ordinal], dataTypes[ordinal], ordinal);
        }

        // Valida contra las columnas registradas y persiste la selección; si la columna
        // no existe lanza UnknownColumnException y el catch externo elimina el archivo.
        dataset.SetPhoneColumn(command.PhoneColumn);

        dataset.SetStoragePath(storagePath);
        await datasetRepository.AddAsync(dataset, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var columnNames = new string[columnCount];

        for (var ordinal = 0; ordinal < columnCount; ordinal++)
        {
            columnNames[ordinal] = dataset.Columns.Single(c => c.Ordinal == ordinal).Name;
        }

        long rowNumber = 0;
        var pendingBatch = new List<DatasetRow>(options.InsertBatchSize);

        foreach (var cells in EnumerateAllRows(reader, sampleRows))
        {
            rowNumber++;
            pendingBatch.Add(BuildRow(dataset.Id, rowNumber, cells, columnNames, dataTypes));

            if (pendingBatch.Count >= options.InsertBatchSize)
            {
                await InsertBatchAsync(pendingBatch, cancellationToken);
                pendingBatch.Clear();
            }
        }

        await InsertBatchAsync(pendingBatch, cancellationToken);

        dataset.SetRowCount(rowNumber);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return dataset.Id;
    }

    private static List<string> ReadAndValidateHeaders(IExcelReader reader)
    {
        var rawHeaders = reader.ReadHeaders();
        var headers = new List<string>(rawHeaders.Count);

        foreach (var raw in rawHeaders)
        {
            // NormalizeColumnName lanza InvalidDatasetFileException si hay encabezados vacíos.
            headers.Add(Dataset.NormalizeColumnName(raw));
        }

        var duplicates = headers
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new DuplicateColumnException(duplicates[0]);
        }

        return headers;
    }

    private List<IReadOnlyList<string?>> BufferSampleRows(IExcelReader reader)
    {
        var sample = new List<IReadOnlyList<string?>>(options.TypeInferenceSampleSize);

        foreach (var row in reader.ReadDataRows())
        {
            sample.Add(row);

            if (sample.Count >= options.TypeInferenceSampleSize)
            {
                break;
            }
        }

        return sample;
    }

    /// <summary>Rinde primero las filas ya leídas para la muestra y luego continúa el streaming.</summary>
    private static IEnumerable<IReadOnlyList<string?>> EnumerateAllRows(
        IExcelReader reader,
        IReadOnlyList<IReadOnlyList<string?>> alreadyBuffered)
    {
        foreach (var buffered in alreadyBuffered)
        {
            yield return buffered;
        }

        foreach (var row in reader.ReadDataRows())
        {
            yield return row;
        }
    }

    private DatasetRow BuildRow(
        Guid datasetId,
        long rowNumber,
        IReadOnlyList<string?> cells,
        IReadOnlyList<string> columnNames,
        IReadOnlyList<ColumnDataType> dataTypes)
    {
        var values = new KeyValuePair<string, string?>[columnNames.Count];

        for (var ordinal = 0; ordinal < columnNames.Count; ordinal++)
        {
            var raw = ordinal < cells.Count ? cells[ordinal] : null;
            values[ordinal] = new(columnNames[ordinal], ColumnTypeInference.Canonicalize(raw, dataTypes[ordinal]));
        }

        return new DatasetRow(datasetId, rowNumber, values);
    }

    private async Task InsertBatchAsync(List<DatasetRow> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await datasetRepository.AddRowsAsync(batch, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Libera las entidades del lote: memoria constante sin importar el tamaño del archivo.
        unitOfWork.ClearTrackedEntities();
    }

    private void TryDeleteQuietly(string storagePath)
    {
        try
        {
            fileStorage.Delete(storagePath);
        }
        catch
        {
            // Limpieza best-effort; no enmascara el error original.
        }
    }
}
