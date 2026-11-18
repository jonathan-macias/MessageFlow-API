using System.Collections.Concurrent;

namespace MessageFlow.Api.Observability;

/// <summary>
/// Un error 5xx tal como lo ve el investigador de incidentes (Sentinel/Hermes). Solo
/// lleva lo necesario para diagnosticar: nunca el cuerpo de la petición ni datos del usuario.
/// </summary>
public sealed record RecentError(
    DateTimeOffset OccurredAt,
    string Method,
    string Path,
    int StatusCode,
    string ExceptionType,
    string Message,
    string? TraceId);

/// <summary>
/// Cola circular en memoria con los últimos errores 5xx. Es deliberadamente simple: se
/// pierde al reiniciar y basta para que un investigador vea "qué está fallando ahora".
/// </summary>
public sealed class RecentErrorsBuffer(int capacity = 200)
{
    private readonly ConcurrentQueue<RecentError> _errors = new();

    public void Add(RecentError error)
    {
        _errors.Enqueue(error);

        while (_errors.Count > capacity && _errors.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<RecentError> Since(DateTimeOffset since, int max)
        => _errors
            .Where(e => e.OccurredAt >= since)
            .OrderByDescending(e => e.OccurredAt)
            .Take(max)
            .ToList();
}
