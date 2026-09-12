namespace MessageFlow.Application.Abstractions.Messaging;

/// <summary>Marcador de comando: una operación que modifica estado.</summary>
public interface ICommand
{
}

/// <summary>Comando que devuelve una respuesta al completarse.</summary>
public interface ICommand<TResponse> : ICommand
{
}
