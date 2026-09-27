using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Domain.AI;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Application.AI;

/// <summary>
/// Executes structured dataset queries safely using EF Core.
/// Never generates raw SQL — all queries are built programmatically.
/// </summary>
public sealed class DatasetQueryExecutor(
    IDatasetRepository datasetRepository)
{
    /// <summary>
    /// Executes a dataset query and returns the results.
    /// </summary>
    public async Task<DatasetQueryResult> ExecuteAsync(
        Guid datasetId,
        DatasetQuery query,
        IReadOnlyList<Domain.Datasets.ColumnDefinition> columns,
        CancellationToken cancellationToken = default)
    {
        var columnDict = columns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);

        return query.Action switch
        {
            DatasetQueryAction.Count => await ExecuteCountAsync(datasetId, query, columnDict, cancellationToken),
            DatasetQueryAction.Sum => await ExecuteSumAsync(datasetId, query, columnDict, cancellationToken),
            DatasetQueryAction.Average => await ExecuteAverageAsync(datasetId, query, columnDict, cancellationToken),
            DatasetQueryAction.Min => await ExecuteMinAsync(datasetId, query, columnDict, cancellationToken),
            DatasetQueryAction.Max => await ExecuteMaxAsync(datasetId, query, columnDict, cancellationToken),
            DatasetQueryAction.GroupBy => await ExecuteGroupByAsync(datasetId, query, columnDict, cancellationToken),
            DatasetQueryAction.Top => await ExecuteTopAsync(datasetId, query, columnDict, cancellationToken),
            DatasetQueryAction.Search => await ExecuteSearchAsync(datasetId, query, columnDict, cancellationToken),
            _ => throw new DomainException($"Unsupported query action: '{query.Action}'.")
        };
    }

    private async Task<DatasetQueryResult> ExecuteCountAsync(
        Guid datasetId,
        DatasetQuery query,
        Dictionary<string, Domain.Datasets.ColumnDefinition> columnDict,
        CancellationToken cancellationToken)
    {
        var rows = datasetRepository.StreamRowsAsync(datasetId, cancellationToken);
        var filtered = await ApplyFiltersAsync(rows, query.Filters, columnDict, cancellationToken);
        var count = filtered.Count;

        return new DatasetQueryResult
        {
            Query = query,
            Data = new { Count = count }
        };
    }

    private async Task<DatasetQueryResult> ExecuteSumAsync(
        Guid datasetId,
        DatasetQuery query,
        Dictionary<string, Domain.Datasets.ColumnDefinition> columnDict,
        CancellationToken cancellationToken)
    {
        var rows = datasetRepository.StreamRowsAsync(datasetId, cancellationToken);
        var filtered = await ApplyFiltersAsync(rows, query.Filters, columnDict, cancellationToken);

        var sum = filtered
            .Where(r => r.Values.TryGetValue(query.TargetColumn!, out var val) && !string.IsNullOrWhiteSpace(val))
            .Sum(r =>
            {
                r.Values.TryGetValue(query.TargetColumn!, out var val);
                return double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num) ? num : 0;
            });

        return new DatasetQueryResult
        {
            Query = query,
            Data = new { Column = query.TargetColumn, Sum = sum }
        };
    }

    private async Task<DatasetQueryResult> ExecuteAverageAsync(
        Guid datasetId,
        DatasetQuery query,
        Dictionary<string, Domain.Datasets.ColumnDefinition> columnDict,
        CancellationToken cancellationToken)
    {
        var rows = datasetRepository.StreamRowsAsync(datasetId, cancellationToken);
        var filtered = await ApplyFiltersAsync(rows, query.Filters, columnDict, cancellationToken);

        var validValues = filtered
            .Where(r => r.Values.TryGetValue(query.TargetColumn!, out var val) && !string.IsNullOrWhiteSpace(val))
            .Select(r =>
            {
                r.Values.TryGetValue(query.TargetColumn!, out var val);
                return double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num) ? (double?)num : null;
            })
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        var average = validValues.Count > 0 ? validValues.Average() : 0;

        return new DatasetQueryResult
        {
            Query = query,
            Data = new { Column = query.TargetColumn, Average = average, Count = validValues.Count }
        };
    }

    private async Task<DatasetQueryResult> ExecuteMinAsync(
        Guid datasetId,
        DatasetQuery query,
        Dictionary<string, Domain.Datasets.ColumnDefinition> columnDict,
        CancellationToken cancellationToken)
    {
        var rows = datasetRepository.StreamRowsAsync(datasetId, cancellationToken);
        var filtered = await ApplyFiltersAsync(rows, query.Filters, columnDict, cancellationToken);

        var validValues = filtered
            .Where(r => r.Values.TryGetValue(query.TargetColumn!, out var val) && !string.IsNullOrWhiteSpace(val))
            .Select(r =>
            {
                r.Values.TryGetValue(query.TargetColumn!, out var val);
                return val;
            })
            .ToList();

        var min = validValues.Count > 0 ? validValues.Min() : null;

        return new DatasetQueryResult
        {
            Query = query,
            Data = new { Column = query.TargetColumn, Min = min }
        };
    }

    private async Task<DatasetQueryResult> ExecuteMaxAsync(
        Guid datasetId,
        DatasetQuery query,
        Dictionary<string, Domain.Datasets.ColumnDefinition> columnDict,
        CancellationToken cancellationToken)
    {
        var rows = datasetRepository.StreamRowsAsync(datasetId, cancellationToken);
        var filtered = await ApplyFiltersAsync(rows, query.Filters, columnDict, cancellationToken);

        var validValues = filtered
            .Where(r => r.Values.TryGetValue(query.TargetColumn!, out var val) && !string.IsNullOrWhiteSpace(val))
            .Select(r =>
            {
                r.Values.TryGetValue(query.TargetColumn!, out var val);
                return val;
            })
            .ToList();

        var max = validValues.Count > 0 ? validValues.Max() : null;

        return new DatasetQueryResult
        {
            Query = query,
            Data = new { Column = query.TargetColumn, Max = max }
        };
    }

    private async Task<DatasetQueryResult> ExecuteGroupByAsync(
        Guid datasetId,
        DatasetQuery query,
        Dictionary<string, Domain.Datasets.ColumnDefinition> columnDict,
        CancellationToken cancellationToken)
    {
        var rows = datasetRepository.StreamRowsAsync(datasetId, cancellationToken);
        var filtered = await ApplyFiltersAsync(rows, query.Filters, columnDict, cancellationToken);

        var groups = filtered
            .Where(r => r.Values.TryGetValue(query.GroupByColumn!, out var val) && !string.IsNullOrWhiteSpace(val))
            .GroupBy(r =>
            {
                r.Values.TryGetValue(query.GroupByColumn!, out var val);
                return val ?? string.Empty;
            })
            .Select(g => new { Key = g.Key, Count = (long)g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        return new DatasetQueryResult
        {
            Query = query,
            Data = new { Column = query.GroupByColumn, Groups = groups }
        };
    }

    private async Task<DatasetQueryResult> ExecuteTopAsync(
        Guid datasetId,
        DatasetQuery query,
        Dictionary<string, Domain.Datasets.ColumnDefinition> columnDict,
        CancellationToken cancellationToken)
    {
        var rows = datasetRepository.StreamRowsAsync(datasetId, cancellationToken);
        var filtered = await ApplyFiltersAsync(rows, query.Filters, columnDict, cancellationToken);

        var limit = query.Limit ?? 10;

        var topRows = filtered
            .Where(r => r.Values.TryGetValue(query.TargetColumn!, out var val) && !string.IsNullOrWhiteSpace(val))
            .OrderByDescending(r =>
            {
                r.Values.TryGetValue(query.TargetColumn!, out var val);
                return double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num) ? num : 0;
            })
            .Take(limit)
            .Select(r => new Domain.AI.DatasetRowDto(r.Id, r.RowNumber, r.Values))
            .ToList();

        return new DatasetQueryResult
        {
            Query = query,
            Data = new { Column = query.TargetColumn, Limit = limit },
            Rows = topRows
        };
    }

    private async Task<DatasetQueryResult> ExecuteSearchAsync(
        Guid datasetId,
        DatasetQuery query,
        Dictionary<string, Domain.Datasets.ColumnDefinition> columnDict,
        CancellationToken cancellationToken)
    {
        var rows = datasetRepository.StreamRowsAsync(datasetId, cancellationToken);
        var filtered = await ApplyFiltersAsync(rows, query.Filters, columnDict, cancellationToken);

        var limit = query.Limit ?? 100;

        var resultRows = filtered
            .Take(limit)
            .Select(r => new Domain.AI.DatasetRowDto(r.Id, r.RowNumber, r.Values))
            .ToList();

        return new DatasetQueryResult
        {
            Query = query,
            Data = new { TotalMatches = filtered.Count, Returned = resultRows.Count },
            Rows = resultRows
        };
    }

    private static async Task<List<Domain.Datasets.DatasetRow>> ApplyFiltersAsync(
        IAsyncEnumerable<Domain.Datasets.DatasetRow> rows,
        IReadOnlyList<DatasetQueryFilter> filters,
        Dictionary<string, Domain.Datasets.ColumnDefinition> columnDict,
        CancellationToken cancellationToken)
    {
        var result = new List<Domain.Datasets.DatasetRow>();

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            if (DatasetRowFilterEvaluator.Matches(row.Values, filters, columnDict))
            {
                result.Add(row);
            }
        }

        return result;
    }
}
