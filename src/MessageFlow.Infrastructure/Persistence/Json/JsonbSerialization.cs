using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MessageFlow.Infrastructure.Persistence.Json;

/// <summary>
/// Configuración única de serialización de los VOs del dominio hacia/desde jsonb.
/// No usa DictionaryKeyPolicy: las claves de DatasetRow.Values conservan el nombre
/// exacto de la columna (case-insensitive en consultas por diseño del agregado).
/// </summary>
internal static class JsonbSerialization
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new FlowScheduleJsonConverter());
        options.Converters.Add(new DataTriggerConfigJsonConverter());
        options.Converters.Add(new FilterGroupJsonConverter());
        return options;
    }
}

/// <summary>ValueConverter genérico VO ↔ columna jsonb. Los valores null no atraviesan el convertidor.</summary>
internal sealed class JsonbConverter<T> : ValueConverter<T, string>
    where T : class
{
    public static readonly JsonbConverter<T> Instance = new();

    private JsonbConverter()
        : base(
            value => Serialize(value),
            json => Deserialize(json))
    {
    }

    private static string Serialize(T value)
        => JsonSerializer.Serialize(value, JsonbSerialization.Options);

    // Método (no expresión throw): los árboles de expresión del convertidor no admiten throw-expressions.
    private static T Deserialize(string json)
    {
        var value = JsonSerializer.Deserialize<T>(json, JsonbSerialization.Options);

        return value
            ?? throw new FormatException($"Contenido jsonb inválido para '{typeof(T).Name}'.");
    }
}
