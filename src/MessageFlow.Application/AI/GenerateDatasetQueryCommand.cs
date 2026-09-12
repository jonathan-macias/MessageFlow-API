using FluentValidation;
using MessageFlow.Application.Abstractions;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Domain.AI;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Application.AI;

/// <summary>
/// Command to query a dataset using natural language.
/// The AI converts the question to a structured query, executes it, and generates a response.
/// </summary>
public sealed record GenerateDatasetQueryCommand(
    Guid DatasetId,
    string Question) : ICommand<DatasetQueryResponse>;

/// <summary>
/// Validator for GenerateDatasetQueryCommand.
/// </summary>
public sealed class GenerateDatasetQueryCommandValidator : AbstractValidator<GenerateDatasetQueryCommand>
{
    public GenerateDatasetQueryCommandValidator()
    {
        RuleFor(x => x.DatasetId)
            .NotEmpty().WithMessage("Dataset ID is required.");

        RuleFor(x => x.Question)
            .NotEmpty().WithMessage("Question is required.")
            .MaximumLength(2000).WithMessage("Question cannot exceed 2000 characters.");
    }
}

/// <summary>
/// Handler for GenerateDatasetQueryCommand.
/// Orchestrates the flow: retrieve columns, generate query, execute, generate response.
/// </summary>
public sealed class GenerateDatasetQueryCommandHandler(
    IDatasetRepository datasetRepository,
    IGenerativeAIService aiService,
    DatasetQueryExecutor queryExecutor) : ICommandHandler<GenerateDatasetQueryCommand, DatasetQueryResponse>
{
    public async Task<DatasetQueryResponse> HandleAsync(
        GenerateDatasetQueryCommand command,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate that the dataset exists
        var dataset = await datasetRepository.FindByIdAsync(command.DatasetId, cancellationToken)
            ?? throw new NotFoundException("Dataset", command.DatasetId);

        // 2. Retrieve dataset columns
        var columns = await datasetRepository.GetColumnDefinitionsAsync(command.DatasetId, cancellationToken);

        if (columns.Count == 0)
        {
            throw new DomainException("The selected dataset has no columns. Please upload a dataset with at least one column.");
        }

        // 3. Generate structured query from natural language
        var query = await aiService.GenerateDatasetQueryAsync(
            command.Question,
            columns,
            cancellationToken);

        // 4. Execute the query safely
        var queryResult = await queryExecutor.ExecuteAsync(
            command.DatasetId,
            query,
            columns,
            cancellationToken);

        // 5. Generate natural language response
        var answer = await aiService.GenerateResponseAsync(
            command.Question,
            query,
            queryResult.Data,
            cancellationToken);

        // 6. Build response
        return new DatasetQueryResponse(
            answer,
            new QueryInfo(
                query.Action.ToString(),
                query.TargetColumn,
                query.GroupByColumn,
                query.Limit,
                query.Filters.Select(f => new FilterInfo(f.Column, f.Operator.ToString(), f.Value)).ToList()),
            queryResult.Data,
            queryResult.Rows);
    }
}

/// <summary>
/// Response DTO for dataset query.
/// </summary>
public sealed record DatasetQueryResponse(
    string Answer,
    QueryInfo Query,
    object? Data,
    IReadOnlyList<DatasetRowDto>? Rows);

/// <summary>
/// Query information DTO.
/// </summary>
public sealed record QueryInfo(
    string Action,
    string? TargetColumn,
    string? GroupByColumn,
    int? Limit,
    IReadOnlyList<FilterInfo> Filters);

/// <summary>
/// Filter information DTO.
/// </summary>
public sealed record FilterInfo(
    string Column,
    string Operator,
    string? Value);
