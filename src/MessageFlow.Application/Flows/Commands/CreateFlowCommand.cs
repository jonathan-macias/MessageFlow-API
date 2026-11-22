using System.Globalization;
using FluentValidation;
using MessageFlow.Application.Abstractions;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Domain.Flows;
using MessageFlow.Domain.Messaging;
using MessageFlow.Domain.Scheduling;

namespace MessageFlow.Application.Flows.Commands;

public sealed record CreateFlowCommand(
    string Name,
    string? Description,
    Guid DatasetId,
    Channel Channel,
    string MessageTemplate,
    ScheduleRequest Schedule) : ICommand<Guid>;

public sealed class CreateFlowCommandValidator : AbstractValidator<CreateFlowCommand>
{
    public CreateFlowCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("The Flow name is required.")
            .MaximumLength(Flow.NameMaxLength);

        RuleFor(x => x.Description)
            .MaximumLength(Flow.DescriptionMaxLength);

        RuleFor(x => x.DatasetId).NotEmpty();
        RuleFor(x => x.Channel).IsInEnum();

        RuleFor(x => x.MessageTemplate)
            .NotEmpty().WithMessage("The Flow message is required.")
            .MaximumLength(MessageTemplate.MaxLength);

        RuleFor(x => x.Schedule).NotNull().SetValidator(new ScheduleRequestValidator());
    }
}

/// <summary>Validación estructural de la configuración de scheduling. La semántica fina vive en Domain.</summary>
public sealed class ScheduleRequestValidator : AbstractValidator<ScheduleRequest>
{
    private const string TimeOfDayPattern = @"^([01]\d|2[0-3]):[0-5]\d$";
    private const string TimeZonePattern = @"^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+)+$";

    public ScheduleRequestValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.TimeZone)
            .NotEmpty().WithMessage("The time zone is required.")
            .Matches(TimeZonePattern)
            .WithMessage("The time zone must be a valid IANA identifier, for example 'America/Bogota'.");

        When(x => x.Type == ExecutionType.OneTime, () =>
        {
            RuleFor(x => x.RunAtUtc)
                .NotNull().WithMessage("A one-time execution requires an execution date and time.")
                .Must(BeUtc).WithMessage("The execution date must be expressed in UTC (zero offset).");
        });

        When(x => x.Type == ExecutionType.Recurring, () =>
        {
            RuleFor(x => x.RecurrenceTimeOfDay)
                .NotEmpty().WithMessage("A recurring execution requires a time of day in 'HH:mm' format.")
                .Matches(TimeOfDayPattern).WithMessage("The time must be in 'HH:mm' format (00:00 to 23:59).");

            RuleFor(x => x.RecurrenceIntervalDays)
                .InclusiveBetween(RecurrenceRule.MinIntervalDays, RecurrenceRule.MaxIntervalDays)
                .When(x => x.RecurrenceIntervalDays.HasValue);
        });
    }

    private static bool BeUtc(DateTimeOffset? value) => value?.Offset == TimeSpan.Zero;
}

internal static class ScheduleFactory
{
    /// <summary>Construye el VO <see cref="FlowSchedule"/> desde la petición; lanza excepciones de dominio si hay inconsistencias.</summary>
    public static FlowSchedule Build(ScheduleRequest request)
    {
        var zone = TimeZoneId.Create(request.TimeZone);

        return request.Type switch
        {
            ExecutionType.OneTime => FlowSchedule.OneTime(
                request.RunAtUtc ?? throw new DomainException("A one-time execution requires a date and time."),
                zone),
            ExecutionType.Recurring => FlowSchedule.Recurring(
                RecurrenceRule.Daily(ParseTimeOfDay(request.RecurrenceTimeOfDay), request.RecurrenceIntervalDays ?? 1),
                zone),
            ExecutionType.DataTriggered => FlowSchedule.DataTriggered(zone),
            ExecutionType.Immediate => FlowSchedule.Immediate(zone),
            _ => throw new DomainException($"Unknown execution type: '{request.Type}'."),
        };
    }

    private static TimeOnly ParseTimeOfDay(string? value)
        => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new DomainException($"The time '{value}' is not in the expected 'HH:mm' format.");
}

public sealed class CreateFlowCommandHandler(
    IFlowRepository flowRepository,
    IDatasetRepository datasetRepository,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser) : ICommandHandler<CreateFlowCommand, Guid>
{
    public async Task<Guid> HandleAsync(CreateFlowCommand command, CancellationToken cancellationToken = default)
    {
        var dataset = await datasetRepository.FindByIdAsync(command.DatasetId, cancellationToken)
            ?? throw new NotFoundException("Dataset", command.DatasetId);

        if (!Enum.IsDefined(command.Channel))
        {
            throw new DomainException($"Unknown channel: '{command.Channel}'.");
        }

        var template = MessageTemplate.Create(command.MessageTemplate);
        var columns = await datasetRepository.GetColumnDefinitionsAsync(dataset.Id, cancellationToken);
        TemplateVariableGuard.EnsureSupported(template, columns);

        var schedule = ScheduleFactory.Build(command.Schedule);

        var flow = Flow.CreateDraft(
            command.Name,
            command.Description,
            dataset.Id,
            command.Channel,
            template,
            schedule,
            currentUser.UserId!);

        await flowRepository.AddAsync(flow, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return flow.Id;
    }
}
