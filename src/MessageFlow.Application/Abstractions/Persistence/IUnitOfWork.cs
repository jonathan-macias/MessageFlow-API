namespace MessageFlow.Application.Abstractions.Persistence;

/// <summary>Confirma los cambios rastreados por el repositorio en una sola transacción.</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Suelta las entidades rastreadas entre lotes de inserción masiva
    /// para mantener el consumo de memoria constante.
    /// </summary>
    void ClearTrackedEntities();
}
