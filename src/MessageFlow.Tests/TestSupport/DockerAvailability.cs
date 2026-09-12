using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace MessageFlow.Tests.TestSupport;

/// <summary>
/// Disponibilidad de Docker en el entorno actual. Los tests de integración con
/// PostgreSQL (Testcontainers) se ejecutan normalmente donde haya daemon; en su
/// ausencia se omiten automáticamente, igual que los tests de Excel.
/// </summary>
public static class DockerAvailability
{
    public static readonly bool IsAvailable = Probe();

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                "docker",
                ["version", "--format", "{{.Server.Version}}"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            return process is not null
                && process.WaitForExit(10_000)
                && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>Fact que se omite automáticamente si no hay Docker disponible.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = "Docker no está disponible en este entorno; los tests de integración con PostgreSQL se ejecutan en CI.";
        }
    }
}
