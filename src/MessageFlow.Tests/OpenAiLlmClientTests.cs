using System.Net;
using System.Text.Json;
using MessageFlow.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageFlow.Tests;

/// <summary>Cliente de OpenAI: forma de la petición, extracción del texto y clasificación de errores.</summary>
public class OpenAiLlmClientTests
{
    private const string ApiKey = "sk-test-secret";

    private static OpenAiLlmClient Build(StubHttpHandler handler, string model = "gpt-4.1-mini")
        => new(
            new HttpClient(handler),
            new OpenAiOptions { ApiKey = ApiKey, Model = model, BaseUrl = "https://openai.test/" },
            NullLogger<OpenAiLlmClient>.Instance);

    private static string Reply(string content, string finish = "stop")
        => JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = finish, message = new { role = "assistant", content } } },
        });

    [Fact]
    public async Task Envia_chat_completions_con_bearer_json_mode_y_ambos_mensajes()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Reply("""{"ok":true}"""));

        var text = await Build(handler).CompleteAsync("SYSTEM RULES", "USER PROMPT json");

        Assert.Equal("""{"ok":true}""", text);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(ApiKey, request.Headers.Authorization.Parameter);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var root = body.RootElement;
        Assert.Equal("gpt-4.1-mini", root.GetProperty("model").GetString());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(1024, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(0.7, root.GetProperty("temperature").GetDouble());

        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("SYSTEM RULES", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("USER PROMPT json", messages[1].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("gpt-5-mini", false)]
    [InlineData("o4-mini", false)]
    [InlineData("gpt-4.1-mini", true)]
    [InlineData("gpt-4o-mini", true)]
    public async Task La_temperatura_solo_se_envia_a_modelos_que_la_admiten(string model, bool sent)
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Reply("{}"));

        await Build(handler, model).CompleteAsync("s", "p");

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(sent, body.RootElement.TryGetProperty("temperature", out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, LlmFailureKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, LlmFailureKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, LlmFailureKind.Transient)]
    [InlineData(HttpStatusCode.Unauthorized, LlmFailureKind.ProviderRejected)]
    [InlineData(HttpStatusCode.NotFound, LlmFailureKind.ProviderRejected)]
    [InlineData(HttpStatusCode.BadRequest, LlmFailureKind.RequestFailed)]
    public async Task Los_errores_http_se_clasifican_y_no_filtran_el_cuerpo_ni_la_clave(
        HttpStatusCode status,
        LlmFailureKind expected)
    {
        var handler = new StubHttpHandler(status, "{\"error\":{\"message\":\"detalle interno " + ApiKey + "\"}}");

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => Build(handler).CompleteAsync("s", "p"));

        Assert.Equal(expected, ex.Kind);
        Assert.DoesNotContain("detalle interno", ex.Message);
        Assert.DoesNotContain(ApiKey, ex.Message);
    }

    [Fact]
    public async Task Una_cuota_agotada_es_rechazo_del_proveedor_y_no_transitorio()
    {
        var handler = new StubHttpHandler(
            HttpStatusCode.TooManyRequests,
            """{"error":{"code":"insufficient_quota","message":"You exceeded your current quota"}}""");

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => Build(handler).CompleteAsync("s", "p"));

        Assert.Equal(LlmFailureKind.ProviderRejected, ex.Kind);
    }

    [Fact]
    public async Task Propaga_Retry_After_del_proveedor()
    {
        var handler = new StubHttpHandler(HttpStatusCode.TooManyRequests, "{}") { RetryAfter = TimeSpan.FromSeconds(3) };

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => Build(handler).CompleteAsync("s", "p"));

        Assert.Equal(TimeSpan.FromSeconds(3), ex.RetryAfter);
    }

    [Fact]
    public async Task Un_fallo_de_red_es_transitorio()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, "{}") { ThrowOnSend = new HttpRequestException("connection refused") };

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => Build(handler).CompleteAsync("s", "p"));

        Assert.Equal(LlmFailureKind.Transient, ex.Kind);
        Assert.DoesNotContain("connection refused", ex.Message);
    }

    [Fact]
    public async Task Un_timeout_es_transitorio_pero_una_cancelacion_del_llamante_se_propaga()
    {
        var timeout = new StubHttpHandler(HttpStatusCode.OK, "{}") { ThrowOnSend = new TaskCanceledException("timeout") };
        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => Build(timeout).CompleteAsync("s", "p"));
        Assert.Equal(LlmFailureKind.Transient, ex.Kind);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = new StubHttpHandler(HttpStatusCode.OK, "{}") { ThrowOnSend = new TaskCanceledException("cancelled", null, cts.Token) };
        await Assert.ThrowsAsync<TaskCanceledException>(() => Build(cancelled).CompleteAsync("s", "p", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Respuesta_truncada_rechazo_o_vacia_son_RequestFailed()
    {
        var truncated = new StubHttpHandler(HttpStatusCode.OK, Reply("{\"a\":", finish: "length"));
        var refusal = new StubHttpHandler(
            HttpStatusCode.OK,
            """{"choices":[{"finish_reason":"stop","message":{"content":null,"refusal":"No puedo ayudar"}}]}""");
        var empty = new StubHttpHandler(HttpStatusCode.OK, """{"choices":[]}""");

        foreach (var handler in new[] { truncated, refusal, empty })
        {
            var ex = await Assert.ThrowsAsync<LlmProviderException>(() => Build(handler).CompleteAsync("s", "p"));
            Assert.Equal(LlmFailureKind.RequestFailed, ex.Kind);
        }
    }

    [Fact]
    public async Task Sin_clave_o_modelo_falla_como_rechazo_del_proveedor_sin_llamar_a_la_red()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, Reply("{}"));
        var client = new OpenAiLlmClient(
            new HttpClient(handler),
            new OpenAiOptions { ApiKey = "", Model = "gpt-4.1-mini" },
            NullLogger<OpenAiLlmClient>.Instance);

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => client.CompleteAsync("s", "p"));

        Assert.Equal(LlmFailureKind.ProviderRejected, ex.Kind);
        Assert.Empty(handler.Requests);
    }
}
