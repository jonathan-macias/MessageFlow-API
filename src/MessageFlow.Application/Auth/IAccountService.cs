namespace MessageFlow.Application.Auth;

/// <summary>
/// Operaciones de cuenta de usuario: registro, login, resolución por Google.
/// Implementada en Infrastructure sobre ASP.NET Core Identity.
/// </summary>
public interface IAccountService
{
    /// <summary>
    /// Registra un usuario local con email + contraseña.
    /// Devuelve el UserId o lanza excepción con el error de Identity.
    /// </summary>
    Task<(Guid UserId, string Email)> RegisterAsync(string email, string password, CancellationToken ct = default);

    /// <summary>
    /// Valida credenciales y devuelve el usuario si son correctas.
    /// </summary>
    Task<(Guid UserId, string Email)?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default);

    /// <summary>
    /// Busca o crea un usuario a partir de un login externo (Google).
    /// Devuelve el UserId, email y si fue creado recientemente.
    /// </summary>
    Task<(Guid UserId, string Email, bool IsNewUser)> FindOrCreateExternalAsync(
        string provider,
        string externalId,
        string email,
        string? displayName,
        CancellationToken ct = default);
}
