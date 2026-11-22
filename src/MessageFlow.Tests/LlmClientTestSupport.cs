using System.Net;
using MessageFlow.Infrastructure.AI;

namespace MessageFlow.Tests;

/// <summary>Handler HTTP falso para los clientes de IA: captura la petición y devuelve una respuesta fija.</summary>
internal sealed class StubHttpHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    public Exception? ThrowOnSend { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (ThrowOnSend is not null)
        {
            throw ThrowOnSend;
        }

        Requests.Add(request);
        Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));

        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
        };

        if (RetryAfter is { } retryAfter)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
        }

        return response;
    }
}

/// <summary>Cliente de IA falso: reproduce una secuencia de resultados (texto o fallo) y cuenta las llamadas.</summary>
internal sealed class ScriptedLlmClient(string provider, params object[] script) : ILlmClient
{
    private readonly Queue<object> _script = new(script);

    public string Provider => provider;

    public int Calls { get; private set; }

    public Task<string> CompleteAsync(string systemInstruction, string prompt, LlmOutputFormat format = LlmOutputFormat.Json, CancellationToken cancellationToken = default)
    {
        Calls++;

        var next = _script.Count > 0 ? _script.Dequeue() : throw new InvalidOperationException("Script exhausted.");

        return next switch
        {
            string text => Task.FromResult(text),
            Exception ex => Task.FromException<string>(ex),
            _ => throw new InvalidOperationException("Unsupported script item."),
        };
    }

    public static LlmProviderException Failure(LlmFailureKind kind, TimeSpan? retryAfter = null)
        => new($"{kind} failure", kind, retryAfter);
}
