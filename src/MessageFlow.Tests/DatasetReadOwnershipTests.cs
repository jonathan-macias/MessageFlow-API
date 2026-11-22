using MessageFlow.Application.Common;
using MessageFlow.Application.Datasets.Queries;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Tests.TestSupport;

namespace MessageFlow.Tests;

/// <summary>
/// Las lecturas de filas y columnas de un dataset deben validar la propiedad del dataset
/// (FindByIdAsync filtra por propietario); los métodos de filas/columnas del repositorio
/// no lo hacen. Un dataset ajeno debe ser indistinguible de uno inexistente.
/// </summary>
public class DatasetReadOwnershipTests
{
    [Fact]
    public async Task Filas_de_un_dataset_no_visible_para_el_usuario_responden_NotFound()
    {
        // El dataset no está en el repositorio visible para el usuario (equivale a un dataset ajeno).
        var repository = new FakeDatasetRepository();
        var handler = new GetDatasetRowsQueryHandler(repository);

        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(
            new GetDatasetRowsQuery(Guid.NewGuid(), new PageRequest { Page = 1, PageSize = 10 })));
    }

    [Fact]
    public async Task Columnas_de_un_dataset_no_visible_para_el_usuario_responden_NotFound()
    {
        var repository = new FakeDatasetRepository();
        repository.Load(
            [new ColumnDefinition(Guid.NewGuid(), "Telefono", ColumnDataType.Text)],
            []);

        var handler = new GetDatasetColumnsQueryHandler(repository);

        // Antes de la corrección devolvía las columnas del dataset ajeno.
        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(
            new GetDatasetColumnsQuery(Guid.NewGuid())));
    }

    [Fact]
    public async Task Columnas_de_un_dataset_propio_se_devuelven()
    {
        var dataset = Dataset.Create("Contactos", "contactos.xlsx", "user-a");
        var column = new ColumnDefinition(Guid.NewGuid(), "Telefono", ColumnDataType.Text);

        var repository = new FakeDatasetRepository();
        repository.Load(dataset);
        repository.Load([column], []);

        var handler = new GetDatasetColumnsQueryHandler(repository);

        var result = await handler.HandleAsync(new GetDatasetColumnsQuery(dataset.Id));

        var dto = Assert.Single(result);
        Assert.Equal("Telefono", dto.Name);
    }
}
