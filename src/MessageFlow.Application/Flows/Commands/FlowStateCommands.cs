using FluentValidation;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;

namespace MessageFlow.Application.Flows.Commands;

// Transiciones de estado del Flow (§18): Draft→Active→Paused↔Active, *→Archived.
// Las reglas de transición viven en el agregado; estos handlers solo orquestan.

public sealed record ActivateFlowCommand(Guid FlowId) : ICommand, IFlowIdHolder;

public sealed record PauseFlowCommand(Guid FlowId) : ICommand, IFlowIdHolder;

public sealed record ArchiveFlowCommand(Guid FlowId) : ICommand, IFlowIdHolder;

/// <summary>Eliminación física permitida solo en Draft/Archived (ver Flow.EnsureDeletable).</summary>
public sealed record DeleteFlowCommand(Guid FlowId) : ICommand, IFlowIdHolder;

public interface IFlowIdHolder
{
    Guid FlowId { get; }
}

public abstract class FlowIdValidatorBase<T> : AbstractValidator<T>
    where T : IFlowIdHolder
{
    protected FlowIdValidatorBase()
    {
        RuleFor(x => x.FlowId).NotEmpty();
    }
}

public sealed class ActivateFlowCommandValidator : FlowIdValidatorBase<ActivateFlowCommand>
{
}

public sealed class PauseFlowCommandValidator : FlowIdValidatorBase<PauseFlowCommand>
{
}

public sealed class ArchiveFlowCommandValidator : FlowIdValidatorBase<ArchiveFlowCommand>
{
}

public sealed class DeleteFlowCommandValidator : FlowIdValidatorBase<DeleteFlowCommand>
{
}

public sealed class ActivateFlowCommandHandler(IFlowRepository flowRepository, IUnitOfWork unitOfWork)
    : ICommandHandler<ActivateFlowCommand>
{
    public async Task HandleAsync(ActivateFlowCommand command, CancellationToken cancellationToken = default)
    {
        var flow = await flowRepository.FindByIdAsync(command.FlowId, cancellationToken)
            ?? throw new NotFoundException("Flow", command.FlowId);

        flow.Activate();
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

public sealed class PauseFlowCommandHandler(IFlowRepository flowRepository, IUnitOfWork unitOfWork)
    : ICommandHandler<PauseFlowCommand>
{
    public async Task HandleAsync(PauseFlowCommand command, CancellationToken cancellationToken = default)
    {
        var flow = await flowRepository.FindByIdAsync(command.FlowId, cancellationToken)
            ?? throw new NotFoundException("Flow", command.FlowId);

        flow.Pause();
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

public sealed class ArchiveFlowCommandHandler(IFlowRepository flowRepository, IUnitOfWork unitOfWork)
    : ICommandHandler<ArchiveFlowCommand>
{
    public async Task HandleAsync(ArchiveFlowCommand command, CancellationToken cancellationToken = default)
    {
        var flow = await flowRepository.FindByIdAsync(command.FlowId, cancellationToken)
            ?? throw new NotFoundException("Flow", command.FlowId);

        flow.Archive();
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

public sealed class DeleteFlowCommandHandler(IFlowRepository flowRepository, IUnitOfWork unitOfWork)
    : ICommandHandler<DeleteFlowCommand>
{
    public async Task HandleAsync(DeleteFlowCommand command, CancellationToken cancellationToken = default)
    {
        var flow = await flowRepository.FindByIdAsync(command.FlowId, cancellationToken)
            ?? throw new NotFoundException("Flow", command.FlowId);

        flow.EnsureDeletable();
        await flowRepository.DeleteAsync(flow, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
