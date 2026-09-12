using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MessageFlow.Infrastructure.Persistence;

/// <summary>
/// Permite generar/aplicar migraciones con `dotnet-ef` sin arrancar el host de la API.
/// Usa una cadena de conexión local solo en tiempo de diseño (nunca en runtime).
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MessageFlowDbContext>
{
    public MessageFlowDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MessageFlowDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=messageflow_design_time;Username=postgres;Password=postgres")
            .Options;

        return new MessageFlowDbContext(options);
    }
}
