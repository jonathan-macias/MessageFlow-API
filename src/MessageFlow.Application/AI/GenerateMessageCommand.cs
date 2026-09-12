using FluentValidation;
using MessageFlow.Application.Abstractions;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Application.AI;

/// <summary>
/// Command to generate a WhatsApp message template using AI.
/// The frontend sends only the description, tone, and datasetId.
/// The backend retrieves dataset variables internally.
/// </summary>
public sealed record GenerateMessageCommand(
    string Description,
    string Tone,
    Guid DatasetId) : ICommand<GenerateMessageResponse>;

/// <summary>
/// Validator for GenerateMessageCommand.
/// </summary>
public sealed class GenerateMessageCommandValidator : AbstractValidator<GenerateMessageCommand>
{
    public GenerateMessageCommandValidator()
    {
        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Message description is required.")
            .MaximumLength(2000).WithMessage("Message description cannot exceed 2000 characters.");

        RuleFor(x => x.Tone)
            .NotEmpty().WithMessage("Tone is required.")
            .MaximumLength(50).WithMessage("Tone cannot exceed 50 characters.");

        RuleFor(x => x.DatasetId)
            .NotEmpty().WithMessage("Dataset ID is required.");
    }
}

/// <summary>
/// Handler for GenerateMessageCommand.
/// Retrieves dataset variables, calls AI service, and validates the result.
/// </summary>
public sealed class GenerateMessageCommandHandler(
    IDatasetRepository datasetRepository,
    IGenerativeAIService aiService) : ICommandHandler<GenerateMessageCommand, GenerateMessageResponse>
{
    public async Task<GenerateMessageResponse> HandleAsync(
        GenerateMessageCommand command,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate that the dataset exists
        var dataset = await datasetRepository.FindByIdAsync(command.DatasetId, cancellationToken)
            ?? throw new NotFoundException("Dataset", command.DatasetId);

        // 2. Retrieve dataset variables/columns
        var columns = await datasetRepository.GetColumnDefinitionsAsync(command.DatasetId, cancellationToken);

        if (columns.Count == 0)
        {
            throw new DomainException("The selected dataset has no columns/variables. Please upload a dataset with at least one column.");
        }

        // 3. Call AI service to generate message
        var result = await aiService.GenerateMessageAsync(
            command.Description,
            command.Tone,
            columns,
            cancellationToken);

        // 4. Build response with variable details
        var usedVariables = columns
            .Where(c => result.VariableIds.Contains(c.Id))
            .Select(c => new VariableDto(c.Id, c.Name, (int)c.DataType))
            .ToList();

        return new GenerateMessageResponse(result.MessageTemplate, usedVariables);
    }
}

/// <summary>
/// Response DTO for message generation.
/// </summary>
public sealed record GenerateMessageResponse(
    string MessageTemplate,
    IReadOnlyList<VariableDto> Variables);

/// <summary>
/// Variable DTO with ID, name, and data type.
/// </summary>
public sealed record VariableDto(Guid Id, string Name, int DataType);
