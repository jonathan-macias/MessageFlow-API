using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Scheduling;

/// <summary>
/// Configuración del trigger basado en datos (jsonb): qué columna de fecha del dataset
/// determina cuándo ejecutarse el Flow y con qué modo de coincidencia.
/// Extensible: nuevos modos requieren miembros de <see cref="DateTriggerMatchMode"/>
/// y su evaluación en Application/Infrastructure, sin cambios estructurales aquí.
/// </summary>
public sealed record DataTriggerConfig
{
    private DataTriggerConfig(Guid columnId, DateTriggerMatchMode matchMode)
        => (ColumnId, MatchMode) = (columnId, matchMode);

    /// <summary>Columna del dataset (tipo Date) que dispara la ejecución.</summary>
    public Guid ColumnId { get; }

    public DateTriggerMatchMode MatchMode { get; }

    public static DataTriggerConfig Create(Guid columnId, DateTriggerMatchMode matchMode)
    {
        if (columnId == Guid.Empty)
        {
            throw new DomainException("The data trigger must specify a valid dataset column.");
        }

        if (!Enum.IsDefined(matchMode))
        {
            throw new DomainException($"Unknown date match mode: '{matchMode}'.");
        }

        return new DataTriggerConfig(columnId, matchMode);
    }
}
