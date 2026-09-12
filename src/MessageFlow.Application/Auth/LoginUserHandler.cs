using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Application.Auth;

public sealed class LoginUserHandler(IAccountService accountService, ITokenService tokenService)
    : ICommandHandler<LoginUserCommand, LoginUserResult>
{
    public async Task<LoginUserResult> HandleAsync(LoginUserCommand command, CancellationToken cancellationToken = default)
    {
        var result = await accountService.ValidateCredentialsAsync(command.Email, command.Password, cancellationToken);

        if (result is null)
        {
            throw new DomainException("Credenciales inválidas.");
        }

        var (accessToken, expiresAt) = tokenService.GenerateToken(result.Value.UserId, result.Value.Email);
        return new LoginUserResult(accessToken, expiresAt, result.Value.UserId, result.Value.Email);
    }
}
