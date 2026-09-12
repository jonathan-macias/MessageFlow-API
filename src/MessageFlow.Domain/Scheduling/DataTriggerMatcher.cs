using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Filters;

namespace MessageFlow.Domain.Scheduling;

/// <summary>
/// Evalúa la condición del trigger de datos sobre el valor de una celda (§7).
/// Igual que <see cref="FilterMatcher"/>: se construye una vez, evalúa millones de
/// filas sin validar nada más, y las celdas no parseables simplemente no coinciden
/// (nunca interrumpen un procesamiento masivo). Extensible: nuevos modos agregan
/// miembros al enum y una rama aquí.
/// </summary>
public sealed class DataTriggerMatcher(DateTriggerMatchMode matchMode)
{
    public DateTriggerMatchMode MatchMode { get; } = Enum.IsDefined(matchMode)
        ? matchMode
        : throw new ArgumentOutOfRangeException(nameof(matchMode), matchMode, "Modo de coincidencia desconocido.");

    public static DataTriggerMatcher Create(DataTriggerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new DataTriggerMatcher(config.MatchMode);
    }

    /// <summary>
    /// Indica si la celda del registro coincide con la fecha de referencia (hoy local).
    /// <paramref name="cellValue"/> es el valor crudo de la columna configurada.
    /// </summary>
    public bool Matches(string? cellValue, DateOnly referenceDate) => MatchMode switch
    {
        // Aniversario (cumpleaños): día y mes coinciden, el año se ignora.
        // Nota: cumpleaños del 29-Feb solo coinciden en años bisiestos; regla
        // determinista y documentada, ajustable como nuevo modo si el negocio lo pide.
        DateTriggerMatchMode.AnniversaryDayMonth
            => FilterValueCoercion.TryParseDate(cellValue, out var value)
                && value.Month == referenceDate.Month
                && value.Day == referenceDate.Day,
        _ => false,
    };
}
