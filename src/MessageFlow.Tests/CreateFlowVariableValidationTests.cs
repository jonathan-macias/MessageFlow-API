using MessageFlow.Application.Common;
using MessageFlow.Application.Flows;
using MessageFlow.Application.Flows.Commands;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Tests.TestSupport;

namespace MessageFlow.Tests;

/// <summary>
/// Regla de guardado (§11): la plantilla solo puede referenciar variables que
/// correspondan a columnas del dataset asociado — por nombre o por Id. Un Id de otro
/// dataset (o inventado) NO se guarda y produce el error correspondiente.
/// </summary>
public class CreateFlowVariableValidationTests
{
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeFlowRepository _flows = new();
    private readonly FakeDatasetRepository _datasets = new();

    private readonly Dataset _dataset;
    private readonly Guid _nombreId;
    private readonly Guid _correoId;

    public CreateFlowVariableValidationTests()
    {
        _dataset = Dataset.Create("Contactos", "contactos.xlsx", "owner-1");
        _dataset.AddColumn("Nombre", ColumnDataType.Text, ordinal: 0);
        _dataset.AddColumn("Correo", ColumnDataType.Text, ordinal: 1);

        // Los Ids válidos son los que el agregado asignó realmente a sus columnas.
        _nombreId = _dataset.Columns.First(c => c.Name == "Nombre").Id;
        _correoId = _dataset.Columns.First(c => c.Name == "Correo").Id;

        _datasets.Load(_dataset);
        _datasets.Load(
            [.. _dataset.Columns.OrderBy(c => c.Ordinal).Select(c => new ColumnDefinition(c.Id, c.Name, c.DataType))],
            []);
    }

    [Fact]
    public async Task Un_id_de_otro_dataset_no_permite_guardar_y_reporta_el_token()
    {
        var foreignId = Guid.CreateVersion7();
        var handler = BuildHandler();

        var command = new CreateFlowCommand(
            "Cumpleaños",
            null,
            _dataset.Id,
            Channel.WhatsApp,
            $"Hola {{{{{foreignId}}}}}, tu correo es {{{{{_correoId}}}}}",
            ImmediateSchedule());

        var exception = await Assert.ThrowsAsync<UnknownTemplateVariablesException>(
            () => handler.HandleAsync(command));

        // El error menciona el Id ajeno pero no los legítimos.
        Assert.Contains(exception.VariableNames, v => v.Equals(foreignId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(exception.VariableNames, v => v.Contains(_correoId.ToString(), StringComparison.OrdinalIgnoreCase));

        Assert.Empty(_flows.Store); // no se guardó nada
    }

    [Fact]
    public async Task Un_nombre_inexistente_tampoco_permite_guardar()
    {
        var handler = BuildHandler();

        var command = new CreateFlowCommand(
            "Cumpleaños",
            null,
            _dataset.Id,
            Channel.WhatsApp,
            "Hola {{Vendedor}}",
            ImmediateSchedule());

        var exception = await Assert.ThrowsAsync<UnknownTemplateVariablesException>(
            () => handler.HandleAsync(command));

        Assert.Equal(["Vendedor"], exception.VariableNames);
        Assert.Empty(_flows.Store); // no se guardó nada
    }

    [Fact]
    public async Task Ids_validos_del_propio_dataset_en_formato_mixto_se_guardan_bien()
    {
        var handler = BuildHandler();

        // Id con guiones + Id sin guiones ("N") + nombre: los tres válidos para este dataset.
        var command = new CreateFlowCommand(
            "Cumpleaños",
            null,
            _dataset.Id,
            Channel.WhatsApp,
            $"Hola {{{{{_nombreId}}}}}, correo {{{{{_correoId.ToString("N")}}}}} o {{{{Correo}}}}",
            ImmediateSchedule());

        await handler.HandleAsync(command);

        Assert.Single(_flows.Store);
    }

    [Fact]
    public async Task Un_dataset_inexistente_rechaza_el_guardado()
    {
        var handler = BuildHandler();

        var command = new CreateFlowCommand(
            "Cumpleaños",
            null,
            Guid.CreateVersion7(),
            Channel.WhatsApp,
            "Hola {{Nombre}}",
            ImmediateSchedule());

        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(command));
    }

    private CreateFlowCommandHandler BuildHandler()
        => new(_flows, _datasets, _unitOfWork, new FakeCurrentUser());

    private static ScheduleRequest ImmediateSchedule()
        => new(ExecutionType.Immediate, "UTC");
}
