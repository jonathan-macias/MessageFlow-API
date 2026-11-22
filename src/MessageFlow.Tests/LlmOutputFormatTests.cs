using System.Net;
using System.Text.Json;
using MessageFlow.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageFlow.Tests;

/// <summary>
/// El modo JSON solo se pide cuando se espera un objeto JSON. OpenAI responde 400 si se activa sin que
/// los mensajes contengan la palabra "JSON", cosa que los prompts de respuesta en texto no hacen.
/// </summary>
public class LlmOutputFormatTests
{
    private static OpenAiLlmClient BuildOpenAi(StubHttpHandler handler)
        => new(
            new HttpClient(handler),
            new OpenAiOptions { ApiKey = "k", Model = "gpt-4.1-mini", BaseUrl = "https://openai.test" },
            NullLogger<OpenAiLlmClient>.Instance);

    private static GeminiLlmClient BuildGemini(StubHttpHandler handler)
        => new(
            new HttpClient(handler),
            new GeminiOptions { ApiKey = "k", Model = "m" },
            NullLogger<GeminiLlmClient>.Instance);

    private const string OpenAiReply = """{"choices":[{"finish_reason":"stop","message":{"content":"Hay 3 registros."}}]}""";
    private const string GeminiReply = """{"candidates":[{"content":{"parts":[{"text":"Hay 3 registros."}]}}]}""";

    [Fact]
    public async Task OpenAI_en_modo_Text_no_envia_response_format()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, OpenAiReply);

        var text = await BuildOpenAi(handler).CompleteAsync("s", "Answer the question", LlmOutputFormat.Text);

        Assert.Equal("Hay 3 registros.", text);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task OpenAI_en_modo_Json_envia_json_object()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, OpenAiReply);

        await BuildOpenAi(handler).CompleteAsync("s", "Return a JSON object", LlmOutputFormat.Json);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Gemini_solo_pide_application_json_en_modo_Json()
    {
        var json = new StubHttpHandler(HttpStatusCode.OK, GeminiReply);
        var text = new StubHttpHandler(HttpStatusCode.OK, GeminiReply);

        await BuildGemini(json).CompleteAsync("s", "p", LlmOutputFormat.Json);
        await BuildGemini(text).CompleteAsync("s", "p", LlmOutputFormat.Text);

        using var jsonBody = JsonDocument.Parse(json.Bodies[0]);
        using var textBody = JsonDocument.Parse(text.Bodies[0]);
        Assert.Equal("application/json", jsonBody.RootElement.GetProperty("generation_config").GetProperty("response_mime_type").GetString());
        Assert.False(textBody.RootElement.GetProperty("generation_config").TryGetProperty("response_mime_type", out _));
        Assert.Equal(1024, textBody.RootElement.GetProperty("generation_config").GetProperty("max_output_tokens").GetInt32());
    }

    [Fact]
    public async Task El_respaldo_reenvia_el_formato_al_secundario()
    {
        var primary = new FormatRecordingClient("Gemini", ScriptedLlmClient.Failure(LlmFailureKind.ProviderRejected));
        var secondary = new FormatRecordingClient("OpenAI", "ok");
        var client = new FallbackLlmClient(primary, secondary, NullLogger<FallbackLlmClient>.Instance, TimeSpan.Zero);

        await client.CompleteAsync("s", "p", LlmOutputFormat.Text);

        Assert.Equal(LlmOutputFormat.Text, primary.LastFormat);
        Assert.Equal(LlmOutputFormat.Text, secondary.LastFormat);
    }

    private sealed class FormatRecordingClient(string provider, params object[] script) : ILlmClient
    {
        private readonly Queue<object> _script = new(script);

        public string Provider => provider;

        public LlmOutputFormat LastFormat { get; private set; }

        public Task<string> CompleteAsync(
            string systemInstruction,
            string prompt,
            LlmOutputFormat format = LlmOutputFormat.Json,
            CancellationToken cancellationToken = default)
        {
            LastFormat = format;

            return _script.Dequeue() switch
            {
                string text => Task.FromResult(text),
                Exception ex => Task.FromException<string>(ex),
                _ => throw new InvalidOperationException(),
            };
        }
    }
}
