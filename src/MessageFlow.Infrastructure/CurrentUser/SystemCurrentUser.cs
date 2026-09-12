using MessageFlow.Application.Abstractions;

namespace MessageFlow.Infrastructure.CurrentUser;

/// <summary>
/// Implementación por defecto: sin identidad resuelta (procesos background).
/// La API la reemplaza por HttpContextCurrentUser inyectada por DI.
/// </summary>
internal sealed class SystemCurrentUser : ICurrentUser
{
    public string? UserId => null;
    public string? Email => null;
    public bool IsAuthenticated => false;
}
