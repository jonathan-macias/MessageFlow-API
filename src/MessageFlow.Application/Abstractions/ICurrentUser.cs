namespace MessageFlow.Application.Abstractions;

/// <summary>
/// Identidad del usuario actual, consumida por Infrastructure al rellenar
/// campos de auditoría (CreatedBy/UpdatedBy) y por handlers para ownership.
/// En background (worker/scheduler) devuelve null/false.
/// </summary>
public interface ICurrentUser
{
    /// <summary>Identificador del usuario autenticado (GUID string), o null si no hay contexto de usuario.</summary>
    string? UserId { get; }

    /// <summary>Email del usuario autenticado, o null si no hay contexto.</summary>
    string? Email { get; }

    /// <summary>True si el usuario está autenticado.</summary>
    bool IsAuthenticated { get; }
}
