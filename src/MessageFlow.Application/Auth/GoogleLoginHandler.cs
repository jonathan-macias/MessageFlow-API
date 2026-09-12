using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Application.Auth;

public sealed class GoogleLoginHandler(
    IGoogleTokenValidator googleValidator,
    IAccountService accountService,
    ITokenService tokenService)
    : ICommandHandler<GoogleLoginCommand, LoginUserResult>
{
    public async Task<LoginUserResult> HandleAsync(GoogleLoginCommand command, CancellationToken cancellationToken = default)
    {
        var googleUser = await googleValidator.ValidateAsync(command.IdToken, cancellationToken);

        var (userId, email, _) = await accountService.FindOrCreateExternalAsync(
            provider: "google",
            externalId: googleUser.ExternalId,
            email: googleUser.Email,
            displayName: googleUser.DisplayName,
            cancellationToken);

        var (accessToken, expiresAt) = tokenService.GenerateToken(userId, email);
        return new LoginUserResult(accessToken, expiresAt, userId, email);
    }
}
