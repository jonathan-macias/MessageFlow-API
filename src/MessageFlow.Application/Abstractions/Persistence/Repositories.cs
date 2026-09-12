using MessageFlow.Application.Datasets.Queries;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Flows;

namespace MessageFlow.Application.Abstractions.Persistence;

/// <summary>Persistencia del agregado Flow (implementada por EF Core en Infrastructure).</summary>
public interface IFlowRepository
{
    Task<Flow?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(Flow flow, CancellationToken cancellationToken = default);

    /// <summary>Elimina físicamente el agregado. Solo válido para estados borrador/archivado.</summary>
    Task DeleteAsync(Flow flow, CancellationToken cancellationToken = default);

    /// <summary>Lista paginada ordenada por creación descendente, con el total para paginación.</summary>
    Task<(IReadOnlyList<Flow> Items, int TotalCount)> ListAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flows en estado Active (candidatos a programación del scheduler), ordenados por
    /// creación. El filtrado por tipo de ejecución lo hace el consumidor en memoria.
    /// </summary>
    Task<IReadOnlyList<Flow>> ListActiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Persistencia y lectura de datos de datasets.</summary>
public interface IDatasetRepository
{
    Task<Dataset?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(Dataset dataset, CancellationToken cancellationToken = default);

    /// <summary>Catálogo de columnas proyectado, sin cargar el agregado completo.</summary>
    Task<IReadOnlyList<ColumnDefinition>> GetColumnDefinitionsAsync(Guid datasetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Nombre de la columna telefónica configurada del dataset (o null/vacío si no
    /// tiene). Es la fuente del destinatario durante la ejecución de Flows.
    /// </summary>
    Task<string?> GetPhoneColumnAsync(Guid datasetId, CancellationToken cancellationToken = default);

    /// <summary>Resúmenes (Id, Nombre) de todos los datasets, más recientes primero.</summary>
    Task<IReadOnlyList<(Guid Id, string Name)>> ListSummariesAsync(CancellationToken cancellationToken = default);

    Task<long> CountRowsAsync(Guid datasetId, CancellationToken cancellationToken = default);

    Task<DatasetRow?> FindRowAsync(Guid datasetId, Guid rowId, CancellationToken cancellationToken = default);

    /// <summary>Inserción por lotes de filas (importación streaming §8).</summary>
    Task AddRowsAsync(IEnumerable<DatasetRow> rows, CancellationToken cancellationToken = default);

    /// <summary>Filas paginadas por número ascendente (preview de datos).</summary>
    Task<(IReadOnlyList<DatasetRow> Items, long TotalCount)> ListRowsAsync(
        Guid datasetId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Iteración streaming de todas las filas del dataset (preview de filtros §25,
    /// ejecución de Flows). Nunca carga el dataset completo en memoria.
    /// </summary>
    IAsyncEnumerable<DatasetRow> StreamRowsAsync(Guid datasetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lote de filas con RowNumber estrictamente posterior al cursor (keyset pagination).
    /// Permite procesar datasets enormes en segmentos acotados sin OFFSET ni lecturas
    /// abiertas que bloqueen escrituras concurrentes en la misma conexión.
    /// </summary>
    Task<IReadOnlyList<DatasetRow>> GetRowsAfterAsync(
        Guid datasetId,
        long afterRowNumber,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>Persistencia de ejecuciones.</summary>
public interface IFlowExecutionRepository
{
    Task<FlowExecution?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(FlowExecution execution, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<FlowExecution> Items, int TotalCount)> ListByFlowAsync(
        Guid flowId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Soporte de idempotencia: existencia previa de la clave única.</summary>
    Task<bool> ExistsByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reclamo atómico de una ejecución Pending para procesarla (§17): transición
    /// condicional Pending→Running que solo una instancia gana aunque haya varias
    /// compitiendo. Devuelve false si la ejecución ya fue tomada o cerrada.
    /// </summary>
    Task<bool> TryBeginProcessingAsync(
        Guid flowExecutionId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Ids de ejecuciones Pending (cola del scheduler), más antiguas primero.</summary>
    Task<IReadOnlyList<Guid>> ListPendingIdsAsync(int maxCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ancla del productor: última ocurrencia programada de un Flow (MAX de
    /// ScheduledForUtc entre ejecuciones Scheduled). Null si nunca se programó.
    /// </summary>
    Task<DateTimeOffset?> GetLatestScheduledOccurrenceUtcAsync(Guid flowId, CancellationToken cancellationToken = default);
}
