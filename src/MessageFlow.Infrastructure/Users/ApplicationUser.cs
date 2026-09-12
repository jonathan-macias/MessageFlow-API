using Microsoft.AspNetCore.Identity;

namespace MessageFlow.Infrastructure.Users;

/// <summary>
/// Usuario de la aplicación. IdentityUser<Guid> integra contraseñas, roles,
/// claims y logins externos (Google) con GUID como tipo de clave primaria,
/// consistente con el resto del dominio.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>Nombre visible del usuario (opcional, para UI).</summary>
    public string? DisplayName { get; set; }

    /// <summary>Fuente de registro: "local" (email+contraseña) o "google".</summary>
    public string AuthProvider { get; set; } = "local";

    /// <summary>ID externo del proveedor (p. ej. Google sub). Null para login local.</summary>
    public string? ExternalProviderId { get; set; }
}
