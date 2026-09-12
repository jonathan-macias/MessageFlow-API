using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Domain.Filters;
using MessageFlow.Domain.Messaging;
using MessageFlow.Domain.Scheduling;

namespace MessageFlow.Application.Flows;

/// <summary>Mapeos manuales Domain ↔ DTO (sin AutoMapper: sin dependencias innecesarias).</summary>
public static class FlowMappings
{
    public static ScheduleDto ToDto(this FlowSchedule schedule) => new(
        schedule.Type,
        schedule.Zone.Value,
        schedule.RunAtUtc,
        schedule.Recurrence?.LocalTimeOfDay.ToString("HH:mm"),
        schedule.Recurrence?.IntervalDays);

    public static DataTriggerDto? ToDto(this DataTriggerConfig? trigger)
        => trigger is null ? null : new DataTriggerDto(trigger.ColumnId, trigger.MatchMode);

    /// <summary>Convierte el árbol de filtros del dominio a su representación de transporte.</summary>
    public static FilterGroupRequest? ToRequest(this FilterNode? node) => node switch
    {
        null => null,
        FilterCondition condition => new FilterGroupRequest(
            FilterCompositionOperator.And,
            Conditions: [new FilterConditionRequest(condition.ColumnId, condition.Operator, condition.Value)]),
        FilterGroup group => FromGroup(group),
        _ => throw new InvalidOperationException($"Tipo de nodo de filtro no soportado: '{node.GetType().Name}'."),
    };

    private static FilterGroupRequest FromGroup(FilterGroup group) => new(
        group.Composition,
        Conditions: [.. group.Conditions
            .OfType<FilterCondition>()
            .Select(c => new FilterConditionRequest(c.ColumnId, c.Operator, c.Value))],
        Groups: [.. group.Conditions
            .OfType<FilterGroup>()
            .Select(FromGroup)]);

    /// <summary>Reconstruye el árbol del dominio validando la estructura (profundidad, nodos vacíos).</summary>
    public static FilterGroup ToGroup(this FilterGroupRequest request)
    {
        var children = new List<FilterNode>();

        foreach (var condition in request.Conditions ?? [])
        {
            children.Add(new FilterCondition(condition.ColumnId, condition.Operator, condition.Value));
        }

        foreach (var nested in request.Groups ?? [])
        {
            children.Add(nested.ToGroup());
        }

        return FilterGroup.Create(request.Composition, children);
    }
}

/// <summary>Orquesta la validación de variables de plantilla contra el catálogo de columnas (§11).</summary>
public static class TemplateVariableGuard
{
    public static void EnsureSupported(MessageTemplate template, IReadOnlyList<ColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(columns);

        // Las variables pueden expresarse por Nombre o por Id de columna. Los tokens
        // parseables como Guid solo son válidos si corresponden a UNA columna de ESTE
        // dataset: un Id de otro dataset (o inventado) se rechaza al guardar.
        var names = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = columns.Select(c => c.Id).ToHashSet();

        var missing = template.ExtractVariableNames()
            .Where(variable => !IsValidIdentifier(variable, names, ids))
            .ToList();

        if (missing.Count > 0)
        {
            throw new UnknownTemplateVariablesException(missing);
        }
    }

    private static bool IsValidIdentifier(string variable, ISet<string> names, ISet<Guid> ids)
        => names.Contains(variable)
           || (Guid.TryParse(variable, out var id) && ids.Contains(id));
}

/// <summary>
/// Resolución de variables expresadas como Id de columna: construye el mapa Id→Nombre
/// y normaliza plantillas/valores para que la renderización siempre opere por nombre,
/// con un único punto de traducción reutilizado por el motor y los previews.
/// </summary>
public static class TemplateColumnAliases
{
    /// <summary>
    /// Mapa Id→Nombre en ambos formatos usuales de Guid ("D" con guiones y "N" sin
    /// guiones), insensible a mayúsculas, coherente con el criterio del guard.
    /// </summary>
    public static IReadOnlyDictionary<string, string> IdToName(IReadOnlyList<ColumnDefinition> columns)
        => columns
            .SelectMany(c => new[]
            {
                new KeyValuePair<string, string>(c.Id.ToString("D"), c.Name),
                new KeyValuePair<string, string>(c.Id.ToString("N"), c.Name),
            })
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Devuelve la plantilla con {{IdColumna}} normalizados a {{Nombre}}; si no hay Ids, intacta.</summary>
    public static MessageTemplate Apply(MessageTemplate template, IReadOnlyList<ColumnDefinition> columns)
        => template.WithVariableAliases(IdToName(columns));

    /// <summary>
    /// Traduce las claves de valores de muestra expresadas como Id de columna a su
    /// nombre, manteniendo además las claves originales por nombre.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> TranslateSampleValues(
        IReadOnlyDictionary<string, string?> sampleValues,
        IReadOnlyList<ColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(sampleValues);

        var idToName = IdToName(columns);

        if (sampleValues.Keys.All(key => !idToName.ContainsKey(key)))
        {
            return sampleValues;
        }

        var translated = new Dictionary<string, string?>(sampleValues, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in sampleValues)
        {
            if (idToName.TryGetValue(key, out var columnName))
            {
                translated[columnName] = value;
            }
        }

        return translated;
    }
}
