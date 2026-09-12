using System.Text.Json;
using System.Text.Json.Serialization;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Filters;

namespace MessageFlow.Infrastructure.Persistence.Json;

/// <summary>
/// Serializa el árbol de filtros completo (raíz FilterGroup). Formato discriminado:
/// condición = { kind: 1, columnId, operator, value } · grupo = { kind: 2, composition, children }.
/// Nunca se construye SQL desde este JSON: solo lo interpretan los traductores validados.
/// </summary>
internal sealed class FilterGroupJsonConverter : JsonConverter<FilterGroup>
{
    private const int ConditionKind = 1;
    private const int GroupKind = 2;

    public override FilterGroup Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var group = ReadNode(document.RootElement);

        return group as FilterGroup
            ?? throw new JsonException("El nodo raíz del filtro persistido debe ser un grupo.");
    }

    public override void Write(Utf8JsonWriter writer, FilterGroup value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, ShapeOf(value), options);

    private static object ShapeOf(FilterNode node) => node switch
    {
        FilterCondition condition => new
        {
            kind = ConditionKind,
            columnId = condition.ColumnId,
            @operator = (int)condition.Operator,
            value = condition.Value,
        },
        FilterGroup group => new
        {
            kind = GroupKind,
            composition = (int)group.Composition,
            children = group.Conditions.Select(ShapeOf).ToArray(),
        },
        _ => throw new JsonException($"Tipo de nodo de filtro no serializable: '{node.GetType().Name}'."),
    };

    private static FilterNode ReadNode(JsonElement element)
    {
        if (!element.TryGetProperty("kind", out var kindElement) || !kindElement.TryGetInt32(out var kind))
        {
            throw new JsonException("Nodo de filtro sin 'kind'.");
        }

        switch (kind)
        {
            case ConditionKind:
            {
                var columnId = element.TryGetProperty("columnId", out var column) && column.ValueKind == JsonValueKind.String
                    ? column.GetGuid()
                    : throw new JsonException("Condición de filtro sin 'columnId'.");

                var operatorRaw = element.TryGetProperty("operator", out var op) && op.ValueKind == JsonValueKind.Number
                    ? op.GetInt32()
                    : throw new JsonException("Condición de filtro sin 'operator'.");

                if (!Enum.IsDefined((FilterOperator)operatorRaw))
                {
                    throw new JsonException($"Operador de filtro desconocido: '{operatorRaw}'.");
                }

                var value = element.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String
                    ? val.GetString()
                    : null;

                return new FilterCondition(columnId, (FilterOperator)operatorRaw, value);
            }

            case GroupKind:
            {
                var compositionRaw = element.TryGetProperty("composition", out var comp) && comp.ValueKind == JsonValueKind.Number
                    ? comp.GetInt32()
                    : throw new JsonException("Grupo de filtros sin 'composition'.");

                if (!Enum.IsDefined((FilterCompositionOperator)compositionRaw))
                {
                    throw new JsonException($"Composición desconocida: '{compositionRaw}'.");
                }

                var children = new List<FilterNode>();

                if (element.TryGetProperty("children", out var array) && array.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in array.EnumerateArray())
                    {
                        children.Add(ReadNode(child));
                    }
                }

                return FilterGroup.Create((FilterCompositionOperator)compositionRaw, children);
            }

            default:
                throw new JsonException($"'kind' de nodo de filtro desconocido: '{kind}'.");
        }
    }
}
