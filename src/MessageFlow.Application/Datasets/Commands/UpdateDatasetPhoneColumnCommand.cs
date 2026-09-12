using FluentValidation;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Application.Datasets.Queries;
using MessageFlow.Domain.Datasets;

namespace MessageFlow.Application.Datasets.Commands;

/// <summary>
/// Cambia la columna telefónica configurada de un dataset ya cargado (§7): la nueva
/// columna debe existir entre las columnas del dataset; en caso contrario el dominio
/// rechaza la operación sin modificar el valor actual.
/// </summary>
public sealed record UpdateDatasetPhoneColumnCommand(Guid DatasetId, string PhoneColumn) : ICommand<DatasetDto>;

public sealed class UpdateDatasetPhoneColumnCommandValidator : AbstractValidator<UpdateDatasetPhoneColumnCommand>
{
    public UpdateDatasetPhoneColumnCommandValidator()
    {
        RuleFor(x => x.DatasetId).NotEmpty();

        RuleFor(x => x.PhoneColumn)
            .NotEmpty().WithMessage("La columna telefónica (phoneColumn) es obligatoria.");
    }
}

public sealed class UpdateDatasetPhoneColumnCommandHandler(
    IDatasetRepository datasetRepository,
    IUnitOfWork unitOfWork) : ICommandHandler<UpdateDatasetPhoneColumnCommand, DatasetDto>
{
    public async Task<DatasetDto> HandleAsync(
        UpdateDatasetPhoneColumnCommand command,
        CancellationToken cancellationToken = default)
    {
        var dataset = await datasetRepository.FindByIdAsync(command.DatasetId, cancellationToken)
            ?? throw new NotFoundException("Dataset", command.DatasetId);

        dataset.SetPhoneColumn(command.PhoneColumn);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new DatasetDto(
            dataset.Id,
            dataset.Name,
            dataset.SourceFileName,
            dataset.RowCount,
            dataset.PhoneColumn,
            dataset.CreatedAtUtc);
    }
}
