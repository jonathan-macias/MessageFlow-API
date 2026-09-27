using MessageFlow.Application.Abstractions;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.AI;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MessageFlow.Infrastructure.AI;

/// <summary>
/// Genera y persiste los embeddings de los datasets que alguien pidió buscar por
/// significado.
///
/// El trabajo se hace en background a propósito: indexar 100.000 filas son miles de
/// llamadas al proveedor, y bloquear el import o la primera consulta por eso no es
/// aceptable. Se procesa un dataset por vez, en lotes acotados, con pausa entre lotes
/// para no agotar el rate limit del free tier.
///
/// El indexado arranca únicamente cuando alguien usa la búsqueda semántica sobre un
/// dataset: importar mil datasets no debe gastar cuota por sorpresa.
/// </summary>
public sealed class DatasetEmbeddingWorker(
    IServiceScopeFactory scopeFactory,
    GeminiOptions geminiOptions,
    SemanticSearchOptions searchOptions,
    EmbeddingIndexingOptions indexingOptions,
    ILogger<DatasetEmbeddingWorker> logger) : BackgroundService
{
    /// <summary>
    /// Un solo dataset a la vez. El rate limit es por proyecto, no por dataset, así que
    /// paralelizar no ayudaría: solo competiría consigo mismo.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!indexingOptions.Enabled)
        {
            logger.LogInformation("Semantic search indexing worker is disabled by configuration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(geminiOptions.ApiKey) || string.IsNullOrWhiteSpace(geminiOptions.EmbeddingModel))
        {
            logger.LogWarning(
                "Semantic search indexing worker is not started: 'Gemini:ApiKey' or 'Gemini:EmbeddingModel' is missing.");
            return;
        }

        if (!searchOptions.Enabled)
        {
            logger.LogInformation("Semantic search is disabled by configuration; indexing worker will not run.");
            return;
        }

        var interval = TimeSpan.FromSeconds(indexingOptions.PollIntervalSeconds);
        logger.LogInformation(
            "Semantic search indexing worker started. Batch size {BatchSize}, poll every {Interval}.",
            indexingOptions.BatchSize,
            interval);

        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Un fallo puntual no debe matar el worker: el estado de cada dataset
                // guarda su propio error y se reintenta en la próxima vuelta.
                logger.LogError(ex, "Unexpected failure while indexing dataset embeddings.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("Semantic search indexing worker stopped.");
    }

    private async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return;
        }

        try
        {
            Guid[] datasetIds;

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<IDatasetEmbeddingRepository>();
                datasetIds = [.. await repository.ListDatasetsPendingIndexingAsync(cancellationToken)];
            }

            foreach (var datasetId in datasetIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await IndexDatasetAsync(datasetId, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task IndexDatasetAsync(Guid datasetId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        var stateRepository = provider.GetRequiredService<IDatasetEmbeddingRepository>();
        var datasetRepository = provider.GetRequiredService<IDatasetRepository>();
        var unitOfWork = provider.GetRequiredService<IUnitOfWork>();
        var embeddingService = provider.GetRequiredService<IEmbeddingService>();

        var state = await stateRepository.FindStateAsync(datasetId, cancellationToken);

        if (state is null || state.Status == DatasetEmbeddingStatus.Ready)
        {
            return;
        }

        // El dataset puede haberse borrado entre que se encoló y ahora.
        var dataset = await datasetRepository.FindByIdAsync(datasetId, cancellationToken);

        if (dataset is null)
        {
            logger.LogInformation("Dataset {DatasetId} no longer exists; dropping its embedding state.", datasetId);
            await stateRepository.DeleteByDatasetAsync(datasetId, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        state.MarkPending(dataset.RowCount);
        state.MarkIndexing();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Indexing embeddings for dataset {DatasetId} ('{DatasetName}') from row {Watermark}.",
            datasetId,
            dataset.Name,
            state.LastEmbeddedRowNumber);

        try
        {
            for (var batch = 0; batch < indexingOptions.MaxBatchesPerRun; batch++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rows = await datasetRepository.GetRowsAfterAsync(
                    datasetId,
                    state.LastEmbeddedRowNumber,
                    indexingOptions.BatchSize,
                    cancellationToken);

                if (rows.Count == 0)
                {
                    state.MarkReady();
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    logger.LogInformation(
                        "Dataset {DatasetId} indexed: {EmbeddedRows} rows with vectors.",
                        datasetId,
                        state.EmbeddedRows);
                    return;
                }

                var columns = await datasetRepository.GetColumnDefinitionsAsync(datasetId, cancellationToken);
                var texts = new List<string>(rows.Count);
                var indexableRows = new List<DatasetRow>(rows.Count);

                foreach (var row in rows)
                {
                    var text = DatasetRowEmbeddingTextBuilder.Build(row, columns);

                    if (text.Length == 0)
                    {
                        // Fila sin texto: se salta pero igual se avanza el watermark,
                        // así no se relee en el próximo ciclo.
                        continue;
                    }

                    texts.Add(text);
                    indexableRows.Add(row);
                }

                if (texts.Count > 0)
                {
                    var vectors = await embeddingService.GenerateBatchAsync(
                        texts,
                        EmbeddingPurpose.Document,
                        cancellationToken);

                    var embeddings = new List<DatasetRowEmbedding>(indexableRows.Count);

                    for (var i = 0; i < indexableRows.Count; i++)
                    {
                        embeddings.Add(new DatasetRowEmbedding(
                            datasetId,
                            indexableRows[i].Id,
                            indexableRows[i].RowNumber,
                            texts[i],
                            vectors[i]));
                    }

                    await stateRepository.AddRangeAsync(embeddings, cancellationToken);
                }

                var watermark = rows[^1].RowNumber;
                state.RecordProgress(state.EmbeddedRows + texts.Count, watermark, dataset.RowCount);
                await unitOfWork.SaveChangesAsync(cancellationToken);

                if (indexingOptions.DelayBetweenBatchesMs > 0 && batch < indexingOptions.MaxBatchesPerRun - 1)
                {
                    await Task.Delay(indexingOptions.DelayBetweenBatchesMs, cancellationToken);
                }
            }

            logger.LogInformation(
                "Paused indexing of dataset {DatasetId} at row {Watermark} ({EmbeddedRows}/{TotalRows}). Will continue next cycle.",
                datasetId,
                state.LastEmbeddedRowNumber,
                state.EmbeddedRows,
                state.TotalRows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Un 429 del free tier es lo más probable. Se registra el error y el dataset
            // queda Failed; la siguiente búsqueda lo vuelve a encolar y reintenta desde
            // el watermark, sin repetir el trabajo ya hecho.
            logger.LogError(ex, "Failed to index embeddings for dataset {DatasetId}.", datasetId);
            state.MarkFailed(ex.Message);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
