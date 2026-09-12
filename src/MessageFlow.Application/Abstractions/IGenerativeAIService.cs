using MessageFlow.Domain.AI;
using MessageFlow.Domain.Datasets;

namespace MessageFlow.Application.Abstractions;

/// <summary>
/// Abstraction for AI-powered operations.
/// The implementation calls an external AI provider (e.g., Gemini, OpenAI).
/// </summary>
public interface IGenerativeAIService
{
    /// <summary>
    /// Generates a WhatsApp message template using the provided description and dataset variables.
    /// </summary>
    Task<GeneratedMessageResult> GenerateMessageAsync(
        string description,
        string tone,
        IReadOnlyList<ColumnDefinition> columns,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a structured dataset query from natural language.
    /// </summary>
    Task<DatasetQuery> GenerateDatasetQueryAsync(
        string question,
        IReadOnlyList<ColumnDefinition> columns,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a natural language response from query results.
    /// </summary>
    Task<string> GenerateResponseAsync(
        string originalQuestion,
        DatasetQuery query,
        object? queryData,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result from AI message generation.
/// </summary>
public sealed record GeneratedMessageResult(
    string MessageTemplate,
    IReadOnlyList<Guid> VariableIds);
