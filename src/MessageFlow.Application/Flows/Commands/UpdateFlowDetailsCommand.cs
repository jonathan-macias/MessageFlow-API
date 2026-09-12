using FluentValidation;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Domain.Flows;

namespace MessageFlow.Application.Flows.Commands;

public sealed record UpdateFlowDetailsCommand(Guid FlowId, string Name, string? Description, ScheduleRequest? Schedule = null) : ICommand;

public sealed class UpdateFlowDetailsCommandValidator : AbstractValidator<UpdateFlowDetailsCommand>
{
    public UpdateFlowDetailsCommandValidator()
    {
        RuleFor(x => x.FlowId).NotEmpty();
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("El nombre del Flow es obligatorio.")
            .MaximumLength(Flow.NameMaxLength);

        RuleFor(x => x.Description).MaximumLength(Flow.DescriptionMaxLength);

        RuleFor(x => x.Schedule)
            .SetValidator(new ScheduleRequestValidator()!)
            .When(x => x.Schedule is not null);
    }
}

public sealed class UpdateFlowDetailsCommandHandler(
    IFlowRepository flowRepository,
    IUnitOfWork unitOfWork) : ICommandHandler<UpdateFlowDetailsCommand>
{
    public async Task HandleAsync(UpdateFlowDetailsCommand command, CancellationToken cancellationToken = default)
    {
        var flow = await flowRepository.FindByIdAsync(command.FlowId, cancellationToken)
            ?? throw new NotFoundException("Flow", command.FlowId);

        flow.UpdateDetails(command.Name, command.Description);

        if (command.Schedule is not null)
        {
            var schedule = ScheduleFactory.Build(command.Schedule);
            flow.UpdateSchedule(schedule);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
