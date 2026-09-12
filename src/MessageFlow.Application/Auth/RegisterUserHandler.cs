using MessageFlow.Application.Abstractions.Messaging;

namespace MessageFlow.Application.Auth;

public sealed class RegisterUserHandler(IAccountService accountService)
    : ICommandHandler<RegisterUserCommand, RegisterUserResult>
{
    public async Task<RegisterUserResult> HandleAsync(RegisterUserCommand command, CancellationToken cancellationToken = default)
    {
        var (userId, email) = await accountService.RegisterAsync(command.Email, command.Password, cancellationToken);
        return new RegisterUserResult(userId, email);
    }
}
