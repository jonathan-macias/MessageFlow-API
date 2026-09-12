using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Filters;
using MessageFlow.Domain.Flows;
using MessageFlow.Domain.Messaging;
using MessageFlow.Domain.Scheduling;
using MessageFlow.Infrastructure.Persistence;

namespace MessageFlow.Tests.Integration;

/// <summary>Fábricas de agregados para tests de integración contra PostgreSQL real.</summary>
internal static class IntegrationData
{
    public static async Task<Dataset> CreateDatasetAsync(
        MessageFlowDbContext context,
        bool withBirthDateColumn = false,
        string name = "Contactos",
        string phoneColumn = "Correo")
    {
        var dataset = Dataset.Create(name, $"{name}.xlsx", "integration-test-user");
        dataset.AddColumn("Correo", ColumnDataType.Text, ordinal: 0);
        dataset.AddColumn("Nombre", ColumnDataType.Text, ordinal: 1);

        if (withBirthDateColumn)
        {
            dataset.AddColumn("FechaNacimiento", ColumnDataType.Date, ordinal: 2);
        }

        // La columna telefónica es obligatoria en el modelo persistido.
        dataset.SetPhoneColumn(phoneColumn);

        context.Datasets.Add(dataset);
        await context.SaveChangesAsync();

        return dataset;
    }

    public static async Task<List<DatasetRow>> AddRowsAsync(
        MessageFlowDbContext context,
        Guid datasetId,
        params (string Nombre, string Correo, string? FechaNacimiento)[] cells)
    {
        var rows = cells.Select((cell, index) => new DatasetRow(
            datasetId,
            index + 1,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Nombre"] = cell.Nombre,
                ["Correo"] = cell.Correo,
                ["FechaNacimiento"] = cell.FechaNacimiento,
            })).ToList();

        context.DatasetRows.AddRange(rows);
        await context.SaveChangesAsync();

        return rows;
    }

    public static async Task<Flow> CreateActiveFlowAsync(
        MessageFlowDbContext context,
        Dataset dataset,
        Guid recipientColumnId,
        string template,
        FilterGroup? rootFilter = null,
        FlowSchedule? schedule = null,
        DateTimeOffset? createdAtUtc = null)
    {
        var flow = Flow.CreateDraft(
            $"Flow-{Guid.NewGuid():N}",
            description: null,
            dataset.Id,
            Channel.WhatsApp,
            MessageTemplate.Create(template),
            schedule ?? FlowSchedule.Immediate(TimeZoneId.Create("UTC")),
            "integration-test-user");

        flow.SetRecipientColumn(recipientColumnId);

        if (rootFilter is not null)
        {
            flow.SetRootFilter(rootFilter);
        }

        flow.Activate();

        context.Flows.Add(flow);
        await context.SaveChangesAsync();

        // El interceptor de auditoría estampa CreatedAtUtc con el reloj del sistema al
        // insertar; los tests de scheduling necesitan un ancla determinista, así que se
        // reescribe en una segunda operación (que solo estampa UpdatedAt*).
        if (createdAtUtc.HasValue)
        {
            flow.CreatedAtUtc = createdAtUtc.Value;
            await context.SaveChangesAsync();
        }

        return flow;
    }
}
