using MessageFlow.Infrastructure.CurrentUser;
using MessageFlow.Infrastructure.Persistence.Repositories;
using MessageFlow.Tests.TestSupport;

namespace MessageFlow.Tests.Integration;

/// <summary>
/// El filtro por propietario del repositorio real (PostgreSQL): un usuario solo ve sus datasets,
/// una petición HTTP anónima no ve ninguno, y solo el contexto de sistema (scheduler/indexado)
/// ve todos. Reemplaza a las pruebas que solo simulaban el filtro.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PostgresOwnershipIsolationTests(PostgresDatabaseFixture fixture)
{
    // IntegrationData.CreateDatasetAsync crea los datasets con este propietario.
    private const string OwnerOfTestData = "integration-test-user";

    [RequiresDockerFact]
    public async Task El_propietario_ve_su_dataset()
    {
        var dataset = await IntegrationData.CreateDatasetAsync(fixture.CreateContext());
        var repository = new DatasetRepository(fixture.CreateContext(), new FakeCurrentUser { UserId = OwnerOfTestData });

        Assert.NotNull(await repository.FindByIdAsync(dataset.Id));
        Assert.Contains(await repository.ListSummariesAsync(), d => d.Id == dataset.Id);
    }

    [RequiresDockerFact]
    public async Task Otro_usuario_autenticado_no_ve_el_dataset()
    {
        var dataset = await IntegrationData.CreateDatasetAsync(fixture.CreateContext());
        var repository = new DatasetRepository(fixture.CreateContext(), new FakeCurrentUser { UserId = "otro-usuario" });

        Assert.Null(await repository.FindByIdAsync(dataset.Id));
        Assert.DoesNotContain(await repository.ListSummariesAsync(), d => d.Id == dataset.Id);
    }

    [RequiresDockerFact]
    public async Task Una_peticion_anonima_no_ve_ningun_dataset()
    {
        var dataset = await IntegrationData.CreateDatasetAsync(fixture.CreateContext());
        var repository = new DatasetRepository(fixture.CreateContext(), new AnonymousCurrentUser());

        Assert.Null(await repository.FindByIdAsync(dataset.Id));
        Assert.Empty(await repository.ListSummariesAsync());
        Assert.Equal(0, await repository.CountDatasetsAsync());
    }

    [RequiresDockerFact]
    public async Task El_contexto_de_sistema_ve_los_datasets_de_todos()
    {
        var dataset = await IntegrationData.CreateDatasetAsync(fixture.CreateContext());
        var repository = new DatasetRepository(fixture.CreateContext(), new SystemCurrentUser());

        Assert.NotNull(await repository.FindByIdAsync(dataset.Id));
    }
}
