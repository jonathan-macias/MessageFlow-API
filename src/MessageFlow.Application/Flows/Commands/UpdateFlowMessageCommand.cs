using FluentValidation;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Domain.Messaging;

namespace MessageFlow.Application.Flows.Commands;

/// <summary>Actualiza la plantilla validando sus variables contra el dataset asociado (§11).</summary>
public sealed record UpdateFlowMessageCommand(Guid FlowId, string Message) : ICommand;

public sealed class UpdateFlowMessageCommandValidator : AbstractValidator<UpdateFlowMessageCommand>
{
    public UpdateFlowMessageCommandValidator()
    {
        RuleFor(x => x.FlowId).NotEmpty();

        RuleFor(x => x.Message)
            .NotEmpty().WithMessage("El mensaje del Flow es obligatorio.")
            .MaximumLength(MessageTemplate.MaxLength);
    }
}

public sealed class UpdateFlowMessageCommandHandler(
    IFlowRepository flowRepository,
    IDatasetRepository datasetRepository,
    IUnitOfWork unitOfWork) : ICommandHandler<UpdateFlowMessageCommand>
{
    public async Task HandleAsync(UpdateFlowMessageCommand command, CancellationToken cancellationToken = default)
    {
        var flow = await flowRepository.FindByIdAsync(command.FlowId, cancellationToken)
            ?? throw new NotFoundException("Flow", command.FlowId);

        var template = MessageTemplate.Create(command.Message);
        var columns = await datasetRepository.GetColumnDefinitionsAsync(flow.DatasetId, cancellationToken);
        TemplateVariableGuard.EnsureSupported(template, columns);

        flow.UpdateMessage(template);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
