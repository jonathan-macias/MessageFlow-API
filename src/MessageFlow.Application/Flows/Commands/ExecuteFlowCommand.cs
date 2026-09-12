using FluentValidation;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Flows;

namespace MessageFlow.Application.Flows.Commands;

/// <summary>
/// "Send Now" (§6): valida ejecutabilidad y variables, crea una FlowExecution en estado
/// Pending con clave de idempotencia única y retorna su Id. El procesamiento real ocurre
/// en background (Fase 7/8); la petición HTTP nunca espera el envío.
/// </summary>
public sealed record ExecuteFlowCommand(Guid FlowId) : ICommand<Guid>;

public sealed class ExecuteFlowCommandValidator : AbstractValidator<ExecuteFlowCommand>
{
    public ExecuteFlowCommandValidator()
    {
        RuleFor(x => x.FlowId).NotEmpty();
    }
}

public sealed class ExecuteFlowCommandHandler(
    IFlowRepository flowRepository,
    IDatasetRepository datasetRepository,
    IFlowExecutionRepository executionRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<ExecuteFlowCommand, Guid>
{
    public async Task<Guid> HandleAsync(ExecuteFlowCommand command, CancellationToken cancellationToken = default)
    {
        var flow = await flowRepository.FindByIdAsync(command.FlowId, cancellationToken)
            ?? throw new NotFoundException("Flow", command.FlowId);

        flow.EnsureExecutable();

        var columns = await datasetRepository.GetColumnDefinitionsAsync(flow.DatasetId, cancellationToken);
        TemplateVariableGuard.EnsureSupported(flow.GetMessageTemplate(), columns);

        var totalRecords = await datasetRepository.CountRowsAsync(flow.DatasetId, cancellationToken);
        var idempotencyKey = $"manual:{Guid.NewGuid():N}";

        var execution = FlowExecution.Start(
            flow.Id,
            flow.Schedule.Type,
            TriggerSource.Manual,
            idempotencyKey,
            timeProvider.GetUtcNow(),
            totalRecords);

        await executionRepository.AddAsync(execution, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return execution.Id;
    }
}
