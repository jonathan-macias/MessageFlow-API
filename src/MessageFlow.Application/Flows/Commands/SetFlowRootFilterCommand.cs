using FluentValidation;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Filters;

namespace MessageFlow.Application.Flows.Commands;

/// <summary>Reemplaza (o limpia, si Filter es null) el filtro raíz del Flow.</summary>
public sealed record SetFlowRootFilterCommand(Guid FlowId, FilterGroupRequest? Filter) : ICommand;

public sealed class SetFlowRootFilterCommandValidator : AbstractValidator<SetFlowRootFilterCommand>
{
    public SetFlowRootFilterCommandValidator()
    {
        RuleFor(x => x.FlowId).NotEmpty();

        // El filtro es opcional (null = limpiar); la validación aplica solo cuando viene informado.
#pragma warning disable CS8620
        RuleFor(x => x.Filter)!
            .SetValidator(new FilterGroupRequestValidator())
            .When(x => x.Filter is not null);
#pragma warning restore CS8620
    }
}

public sealed class FilterGroupRequestValidator : AbstractValidator<FilterGroupRequest>
{
    public FilterGroupRequestValidator()
    {
        RuleFor(x => x.Composition).IsInEnum();

        RuleFor(x => x)
            .Must(g => (g.Conditions?.Count ?? 0) + (g.Groups?.Count ?? 0) > 0)
            .WithMessage("Un grupo de filtros debe contener al menos una condición o subgrupo.");

        RuleForEach(x => x.Conditions).NotNull().SetValidator(new FilterConditionRequestValidator());
        RuleForEach(x => x.Groups).NotNull().SetValidator(new FilterGroupRequestValidator());
    }
}

public sealed class FilterConditionRequestValidator : AbstractValidator<FilterConditionRequest>
{
    public FilterConditionRequestValidator()
    {
        RuleFor(x => x.ColumnId).NotEmpty();
        RuleFor(x => x.Operator).IsInEnum();

        RuleFor(x => x.Value)
            .NotEmpty()
            .When(x => FilterOperatorPolicy.RequiresValue(x.Operator))
            .WithMessage("La condición de filtro requiere un valor.");
    }
}

public sealed class SetFlowRootFilterCommandHandler(
    IFlowRepository flowRepository,
    IDatasetRepository datasetRepository,
    IUnitOfWork unitOfWork) : ICommandHandler<SetFlowRootFilterCommand>
{
    public async Task HandleAsync(SetFlowRootFilterCommand command, CancellationToken cancellationToken = default)
    {
        var flow = await flowRepository.FindByIdAsync(command.FlowId, cancellationToken)
            ?? throw new NotFoundException("Flow", command.FlowId);

        var columns = await datasetRepository.GetColumnDefinitionsAsync(flow.DatasetId, cancellationToken);

        flow.SetRootFilter(command.Filter?.ToGroup());
        flow.ValidateRootFilterAgainst(columns);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
