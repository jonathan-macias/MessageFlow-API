using System.Reflection;
using Xunit;

namespace MessageFlow.Tests;

/// <summary>
/// Disponibilidad del runtime de Excel en el entorno actual.
/// Algunos entornos de desarrollo (Windows con Smart App Control activo) bloquean la
/// carga de binarios de terceros sin reputación en la nube de Microsoft. La aplicación
/// es cross-platform y MiniExcel 1.46.0 es administrado puro con target net10.0, por lo
/// que estos tests se ejecutan normalmente en CI/Linux y aquí solo se omiten cuando el
/// entorno impide cargar el ensamblado.
/// </summary>
public static class ExcelRuntimeAvailability
{
    public static readonly bool IsAvailable = Probe();

    private static bool Probe()
    {
        try
        {
            Assembly.Load(new AssemblyName("MiniExcel"));
            return true;
        }
        catch (Exception ex)
            when (ex is FileLoadException or FileNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}

/// <summary>Fact que se omite automáticamente si el entorno bloquea la carga de MiniExcel.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ExcelRuntimeFactAttribute : FactAttribute
{
    public ExcelRuntimeFactAttribute()
    {
        if (!ExcelRuntimeAvailability.IsAvailable)
        {
            Skip = "Carga de MiniExcel bloqueada por Application Control en este entorno de desarrollo; " +
                   "se ejecuta en CI/Linux (sin Smart App Control).";
        }
    }
}
