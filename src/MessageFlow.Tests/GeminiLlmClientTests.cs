using System.Net;
using System.Text.Json;
using MessageFlow.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageFlow.Tests;

/// <summary>Cliente de Gemini tras la extracción: misma petición de siempre y errores clasificados.</summary>
public class GeminiLlmClientTests
{
    private const string ApiKey = "gemini-test-secret";

    private static GeminiLlmClient Build(StubHttpHandler handler)
        => new(
            new HttpClient(handler),
            new GeminiOptions { ApiKey = ApiKey, Model = "gemini-test-model" },
            NullLogger<GeminiLlmClient>.Instance);

    private static string Reply(string text)
        => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } } } } });

    [Fact]
    public async Task Llama_a_generateContent_con_la_clave_en_el_header_y_pide_JSON()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Reply("""{"ok":true}"""));

        var text = await Build(handler).CompleteAsync("SYSTEM RULES", "USER PROMPT");

        Assert.Equal("""{"ok":true}""", text);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/models/gemini-test-model:generateContent", request.RequestUri!.AbsolutePath);
        Assert.Equal([ApiKey], request.Headers.GetValues("x-goog-api-key"));
        Assert.DoesNotContain(ApiKey, request.RequestUri.ToString());

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var root = body.RootElement;
        Assert.Equal("SYSTEM RULES", root.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("USER PROMPT", root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("application/json", root.GetProperty("generation_config").GetProperty("response_mime_type").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, LlmFailureKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, LlmFailureKind.Transient)]
    [InlineData(HttpStatusCode.Forbidden, LlmFailureKind.ProviderRejected)]
    [InlineData(HttpStatusCode.BadRequest, LlmFailureKind.RequestFailed)]
    public async Task Los_errores_http_se_clasifican_y_no_filtran_el_cuerpo(HttpStatusCode status, LlmFailureKind expected)
    {
        var handler = new StubHttpHandler(status, """{"error":{"message":"The model is overloaded. project 12345"}}""");

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => Build(handler).CompleteAsync("s", "p"));

        Assert.Equal(expected, ex.Kind);
        Assert.DoesNotContain("project 12345", ex.Message);
    }

    [Fact]
    public async Task Respuesta_sin_candidatos_o_sin_texto_es_RequestFailed()
    {
        foreach (var body in new[] { """{"candidates":[]}""", """{"candidates":[{"content":{"parts":[{"text":" "}]}}]}""", "{}" })
        {
            var handler = new StubHttpHandler(HttpStatusCode.OK, body);
            var ex = await Assert.ThrowsAsync<LlmProviderException>(() => Build(handler).CompleteAsync("s", "p"));
            Assert.Equal(LlmFailureKind.RequestFailed, ex.Kind);
        }
    }

    [Fact]
    public async Task Un_fallo_de_red_es_transitorio()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{}") { ThrowOnSend = new HttpRequestException("boom") };

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => Build(handler).CompleteAsync("s", "p"));

        Assert.Equal(LlmFailureKind.Transient, ex.Kind);
    }
}
