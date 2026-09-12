using MessageFlow.Application.Datasets;
using MessageFlow.Application.Datasets.Commands;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Tests.TestSupport;

namespace MessageFlow.Tests;

/// <summary>
/// Cambio de columna telefónica sobre un dataset existente: persiste el nuevo valor,
/// rechaza columnas inexistentes sin modificar el actual y devuelve el DTO actualizado.
/// </summary>
public class UpdateDatasetPhoneColumnCommandTests
{
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeDatasetRepository _datasets = new();

    private readonly Dataset _dataset;

    public UpdateDatasetPhoneColumnCommandTests()
    {
        _dataset = Dataset.Create("Contactos", "contactos.xlsx", "owner-1");
        _dataset.AddColumn("Nombre", ColumnDataType.Text, ordinal: 0);
        _dataset.AddColumn("Numero Celular", ColumnDataType.Text, ordinal: 1);
        _dataset.AddColumn("Numero Auxiliar", ColumnDataType.Text, ordinal: 2);
        _dataset.SetPhoneColumn("Numero Celular");

        _datasets.Load(_dataset);
        _datasets.Load(
            [.. _dataset.Columns.OrderBy(c => c.Ordinal).Select(c => new ColumnDefinition(c.Id, c.Name, c.DataType))],
            []);
    }

    private UpdateDatasetPhoneColumnCommandHandler BuildHandler() => new(_datasets, _unitOfWork);

    [Fact]
    public async Task El_cambio_se_persiste_y_retorna_la_informacion_actualizada()
    {
        var result = await BuildHandler().HandleAsync(
            new UpdateDatasetPhoneColumnCommand(_dataset.Id, "Numero Auxiliar"));

        Assert.Equal("Numero Auxiliar", result.PhoneColumn);
        Assert.Equal("Numero Auxiliar", _datasets.Store[_dataset.Id].PhoneColumn);
        Assert.Equal(1, _unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Una_columna_inexistente_es_rechazada_sin_modificar_el_valor()
    {
        await Assert.ThrowsAsync<UnknownColumnException>(
            () => BuildHandler().HandleAsync(new UpdateDatasetPhoneColumnCommand(_dataset.Id, "No Existe")));

        Assert.Equal("Numero Celular", _datasets.Store[_dataset.Id].PhoneColumn); // valor intacto
        Assert.Equal(0, _unitOfWork.SaveCount); // nada que persistir
    }

    [Fact]
    public async Task Un_dataset_inexistente_produce_not_found()
    {
        await Assert.ThrowsAsync<Application.Common.NotFoundException>(
            () => BuildHandler().HandleAsync(
                new UpdateDatasetPhoneColumnCommand(Guid.CreateVersion7(), "Numero Celular")));
    }
}
