using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Scheduling;

namespace MessageFlow.Infrastructure.Persistence.Json;

/// <summary>Serializa FlowSchedule: { type, zone, runAtUtc, recurrence }.</summary>
internal sealed class FlowScheduleJsonConverter : JsonConverter<FlowSchedule>
{
    private sealed record ScheduleShape(int Type, string Zone, DateTimeOffset? RunAtUtc, RecurrenceShape? Recurrence);

    private sealed record RecurrenceShape(int Frequency, string TimeOfDay, int IntervalDays);

    public override FlowSchedule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var shape = new ScheduleShape(
            RequireInt(root, "type"),
            RequireString(root, "zone"),
            root.TryGetProperty("runAtUtc", out var runAt) && runAt.ValueKind != JsonValueKind.Null
                ? runAt.Deserialize<DateTimeOffset>(options)
                : null,
            root.TryGetProperty("recurrence", out var recurrence) && recurrence.ValueKind != JsonValueKind.Null
                ? new RecurrenceShape(
                    RequireInt(recurrence, "frequency"),
                    RequireString(recurrence, "timeOfDay"),
                    RequireInt(recurrence, "intervalDays"))
                : null);

        if (!Enum.IsDefined((ExecutionType)shape.Type))
        {
            throw new JsonException($"Tipo de ejecución desconocido en jsonb: '{shape.Type}'.");
        }

        var zone = TimeZoneId.FromStored(shape.Zone);

        return shape.Type switch
        {
            (int)ExecutionType.OneTime => FlowSchedule.OneTime(
                shape.RunAtUtc ?? throw new JsonException("El schedule OneTime carece de 'runAtUtc'."), zone),
            (int)ExecutionType.Recurring => FlowSchedule.Recurring(ReadRecurrence(shape.Recurrence), zone),
            (int)ExecutionType.DataTriggered => FlowSchedule.DataTriggered(zone),
            (int)ExecutionType.Immediate => FlowSchedule.Immediate(zone),
            _ => throw new JsonException($"Tipo de ejecución no soportado: '{shape.Type}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, FlowSchedule value, JsonSerializerOptions options)
    {
        RecurrenceShape? recurrence = value.Recurrence is { } rule
            ? new RecurrenceShape(
                (int)rule.Frequency,
                rule.LocalTimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture),
                rule.IntervalDays)
            : null;

        JsonSerializer.Serialize(writer, new ScheduleShape((int)value.Type, value.Zone.Value, value.RunAtUtc, recurrence), options);
    }

    private static RecurrenceRule ReadRecurrence(RecurrenceShape? shape)
    {
        if (shape is null || !Enum.IsDefined((RecurrenceFrequency)shape.Frequency))
        {
            throw new JsonException("La recurrencia persistida es inválida.");
        }

        if (!TimeOnly.TryParseExact(shape.TimeOfDay, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            throw new JsonException($"Hora de recurrencia inválida: '{shape.TimeOfDay}'.");
        }

        return RecurrenceRule.Daily(time, shape.IntervalDays);
    }

    private static int RequireInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value))
        {
            throw new JsonException($"Propiedad numérica requerida ausente: '{propertyName}'.");
        }

        return value;
    }

    private static string RequireString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Propiedad de texto requerida ausente: '{propertyName}'.");
        }

        return property.GetString() ?? throw new JsonException($"Propiedad nula: '{propertyName}'.");
    }
}

/// <summary>Serializa DataTriggerConfig: { columnId, matchMode }.</summary>
internal sealed class DataTriggerConfigJsonConverter : JsonConverter<DataTriggerConfig>
{
    public override DataTriggerConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var columnId = root.TryGetProperty("columnId", out var column) && column.ValueKind == JsonValueKind.String
            ? column.GetGuid()
            : throw new JsonException("DataTrigger sin 'columnId'.");

        var matchModeRaw = root.TryGetProperty("matchMode", out var mode) && mode.ValueKind == JsonValueKind.Number
            ? mode.GetInt32()
            : throw new JsonException("DataTrigger sin 'matchMode'.");

        if (!Enum.IsDefined((DateTriggerMatchMode)matchModeRaw))
        {
            throw new JsonException($"Modo de trigger desconocido: '{matchModeRaw}'.");
        }

        return DataTriggerConfig.Create(columnId, (DateTriggerMatchMode)matchModeRaw);
    }

    public override void Write(Utf8JsonWriter writer, DataTriggerConfig value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, new { columnId = value.ColumnId, matchMode = (int)value.MatchMode }, options);
}
