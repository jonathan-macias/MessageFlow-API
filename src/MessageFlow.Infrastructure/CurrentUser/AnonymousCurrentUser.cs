using MessageFlow.Application.Abstractions;

namespace MessageFlow.Infrastructure.CurrentUser;

/// <summary>
/// Petición HTTP sin usuario autenticado. No es un proceso interno: los repositorios solo
/// devuelven filas cuyo propietario coincide con UserId, y aquí es null, así que no ve nada.
/// Evita que un endpoint que pierda la autenticación (p. ej. por un [AllowAnonymous]) exponga
/// los datos de todos los usuarios.
/// </summary>
internal sealed class AnonymousCurrentUser : ICurrentUser
{
    public string? UserId => null;
    public string? Email => null;
    public bool IsAuthenticated => false;
    public bool IsSystem => false;
}
