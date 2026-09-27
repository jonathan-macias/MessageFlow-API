using System.Runtime.CompilerServices;
using MessageFlow.Application.Abstractions;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Datasets.Queries;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Flows;
using Microsoft.EntityFrameworkCore;

namespace MessageFlow.Infrastructure.Persistence.Repositories;

public sealed class FlowRepository(MessageFlowDbContext context, ICurrentUser currentUser) : IFlowRepository
{
    public Task<Flow?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => context.Flows
            .Where(f => f.Id == id && (!currentUser.IsAuthenticated || f.OwnerId == currentUser.UserId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(Flow flow, CancellationToken cancellationToken = default)
        => await context.Flows.AddAsync(flow, cancellationToken);

    public Task DeleteAsync(Flow flow, CancellationToken cancellationToken = default)
    {
        context.Flows.Remove(flow);
        return Task.CompletedTask;
    }

    public async Task<(IReadOnlyList<Flow> Items, int TotalCount)> ListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = context.Flows.AsNoTracking()
            .Where(f => !currentUser.IsAuthenticated || f.OwnerId == currentUser.UserId)
            .OrderByDescending(f => f.CreatedAtUtc);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyList<Flow>> ListActiveAsync(CancellationToken cancellationToken = default)
        => await context.Flows
            .AsNoTracking()
            .Where(f => f.Status == FlowStatus.Active)
            .OrderBy(f => f.CreatedAtUtc)
            .ToListAsync(cancellationToken);
}

public sealed class DatasetRepository(MessageFlowDbContext context, ICurrentUser currentUser) : IDatasetRepository
{
    public Task<Dataset?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => context.Datasets.Include(d => d.Columns)
            .Where(d => d.Id == id && (!currentUser.IsAuthenticated || d.OwnerId == currentUser.UserId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(Dataset dataset, CancellationToken cancellationToken = default)
        => await context.Datasets.AddAsync(dataset, cancellationToken);

    public async Task<IReadOnlyList<ColumnDefinition>> GetColumnDefinitionsAsync(
        Guid datasetId,
        CancellationToken cancellationToken = default)
        => await context.DatasetColumns
            .AsNoTracking()
            .Where(c => c.DatasetId == datasetId)
            .OrderBy(c => c.Ordinal)
            .Select(c => new ColumnDefinition(c.Id, c.Name, c.DataType))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<(Guid Id, string Name)>> ListSummariesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await context.Datasets
            .AsNoTracking()
            .Where(d => !currentUser.IsAuthenticated || d.OwnerId == currentUser.UserId)
            .OrderByDescending(d => d.CreatedAtUtc)
            .Select(d => new { d.Id, d.Name })
            .ToListAsync(cancellationToken);

        return rows.Select(r => (r.Id, r.Name)).ToList();
    }

    public Task<string?> GetPhoneColumnAsync(Guid datasetId, CancellationToken cancellationToken = default)
        => context.Datasets
            .AsNoTracking()
            .Where(d => d.Id == datasetId)
            .Select(d => d.PhoneColumn)
            .SingleOrDefaultAsync(cancellationToken);

    public Task<long> CountRowsAsync(Guid datasetId, CancellationToken cancellationToken = default)
        => context.DatasetRows.LongCountAsync(r => r.DatasetId == datasetId, cancellationToken);

    public Task<DatasetRow?> FindRowAsync(Guid datasetId, Guid rowId, CancellationToken cancellationToken = default)
        => context.DatasetRows.FirstOrDefaultAsync(r => r.DatasetId == datasetId && r.Id == rowId, cancellationToken);

    public async Task<IReadOnlyList<DatasetRow>> GetRowsByIdsAsync(
        Guid datasetId,
        IReadOnlyCollection<Guid> rowIds,
        CancellationToken cancellationToken = default)
    {
        if (rowIds.Count == 0)
        {
            return [];
        }

        return await context.DatasetRows
            .AsNoTracking()
            .Where(r => r.DatasetId == datasetId && rowIds.Contains(r.Id))
            .OrderBy(r => r.RowNumber)
            .ToListAsync(cancellationToken);
    }

    public async Task AddRowsAsync(IEnumerable<DatasetRow> rows, CancellationToken cancellationToken = default)
        => await context.DatasetRows.AddRangeAsync(rows, cancellationToken);

    public async Task<(IReadOnlyList<DatasetRow> Items, long TotalCount)> ListRowsAsync(
        Guid datasetId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = context.DatasetRows
            .AsNoTracking()
            .Where(r => r.DatasetId == datasetId)
            .OrderBy(r => r.RowNumber);

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async IAsyncEnumerable<DatasetRow> StreamRowsAsync(
        Guid datasetId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rows = context.DatasetRows
            .AsNoTracking()
            .Where(r => r.DatasetId == datasetId)
            .OrderBy(r => r.RowNumber)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken);

        await foreach (var row in rows)
        {
            yield return row;
        }
    }

    public async Task<IReadOnlyList<DatasetRow>> GetRowsAfterAsync(
        Guid datasetId,
        long afterRowNumber,
        int limit,
        CancellationToken cancellationToken = default)
        => await context.DatasetRows
            .AsNoTracking()
            .Where(r => r.DatasetId == datasetId && r.RowNumber > afterRowNumber)
            .OrderBy(r => r.RowNumber)
            .Take(limit)
            .ToListAsync(cancellationToken);
}

public sealed class FlowExecutionRepository(MessageFlowDbContext context, ICurrentUser currentUser) : IFlowExecutionRepository
{
    public Task<FlowExecution?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => context.FlowExecutions
            .Where(e => e.Id == id
                && (!currentUser.IsAuthenticated
                    || context.Flows.Any(f => f.Id == e.FlowId && f.OwnerId == currentUser.UserId)))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(FlowExecution execution, CancellationToken cancellationToken = default)
        => await context.FlowExecutions.AddAsync(execution, cancellationToken);

    public async Task<(IReadOnlyList<FlowExecution> Items, int TotalCount)> ListByFlowAsync(
        Guid flowId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = context.FlowExecutions
            .AsNoTracking()
            .Where(e => e.FlowId == flowId
                && (!currentUser.IsAuthenticated
                    || context.Flows.Any(f => f.Id == e.FlowId && f.OwnerId == currentUser.UserId)))
            .OrderByDescending(e => e.StartedAtUtc);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<bool> ExistsByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default)
        => context.FlowExecutions.AnyAsync(e => e.IdempotencyKey == idempotencyKey, cancellationToken);

    /// <summary>
    /// Reclamo atómico multi-instancia (§17): el UPDATE condicional garantiza que una
    /// sola instancia pase de Pending a Running aunque varias compitan por la misma
    /// ejecución. Ejecutar como set-operation omite el interceptor de auditoría; el
    /// instante de inicio se fija aquí de forma explícita.
    /// </summary>
    public async Task<bool> TryBeginProcessingAsync(
        Guid flowExecutionId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var affected = await context.FlowExecutions
            .Where(e => e.Id == flowExecutionId && e.Status == ExecutionStatus.Pending)
            .ExecuteUpdateAsync(update => update
                .SetProperty(e => e.Status, ExecutionStatus.Running)
                .SetProperty(e => e.StartedAtUtc, startedAtUtc),
                cancellationToken);

        return affected > 0;
    }

    public async Task<IReadOnlyList<Guid>> ListPendingIdsAsync(int maxCount, CancellationToken cancellationToken = default)
        => await context.FlowExecutions
            .AsNoTracking()
            .Where(e => e.Status == ExecutionStatus.Pending)
            .OrderBy(e => e.StartedAtUtc).ThenBy(e => e.Id)
            .Take(maxCount)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

    public Task<DateTimeOffset?> GetLatestScheduledOccurrenceUtcAsync(Guid flowId, CancellationToken cancellationToken = default)
        => context.FlowExecutions
            .AsNoTracking()
            .Where(e => e.FlowId == flowId
                        && e.TriggerSource == TriggerSource.Scheduled
                        && e.ScheduledForUtc != null)
            .MaxAsync(e => e.ScheduledForUtc, cancellationToken);
}
