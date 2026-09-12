using System.Net;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Messaging;
using MessageFlow.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageFlow.Tests;

/// <summary>
/// Cliente HTTP de Evolution API: endpoint, header apiKey, cuerpo number/text y
/// traducción de errores HTTP/red a DeliveryResult.Failure (sin excepciones al motor).
/// </summary>
public class EvolutionApiClientTests
{
    private const string Instance = "mi-instancia";
    private const string ApiKey = "clave-global-123";

    private static EvolutionApiClient BuildClient(FakeHttpHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://evolution.test/"),
            Timeout = TimeSpan.FromSeconds(5),
        };

        return new EvolutionApiClient(
            httpClient,
            new EvolutionApiOptions
            {
                BaseUrl = "https://evolution.test",
                Instance = Instance,
                GlobalApiKey = ApiKey,
            },
            NullLogger<EvolutionApiClient>.Instance);
    }

    [Fact]
    public async Task Envia_POST_al_path_correcto_con_header_y_cuerpo_number_text()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, """{"key":{"id":"MSG-1"}}""");
        var client = BuildClient(handler);

        var message = OutboundMessage.Create(new Recipient(Guid.CreateVersion7(), "3001234567"),
            "Hola Carlos, tu pedido va en camino.", Channel.WhatsApp);

        var result = await client.SendAsync(message);

        Assert.True(result.IsSuccess, $"IsSuccess=false, Error='{result.Error}'");
        Assert.Equal("MSG-1", result.ProviderMessageId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/message/sendText/{Instance}", request.RequestUri!.AbsolutePath);

        Assert.True(request.Headers.TryGetValues("apiKey", out var apiKeys));
        Assert.Equal([ApiKey], apiKeys);

        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"number\":\"3001234567\"", body);
        Assert.Contains("\"text\":\"Hola Carlos, tu pedido va en camino.\"", body);
    }

    [Fact]
    public async Task Un_error_http_se_traduce_a_failure_sin_lanzar_excepcion()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.InternalServerError, "{\"error\":\"instance offline\"}");
        var client = BuildClient(handler);

        var result = await client.SendAsync(OutboundMessage.Create(
            new Recipient(Guid.CreateVersion7(), "3000000000"), "Hola", Channel.WhatsApp));

        Assert.False(result.IsSuccess);
        Assert.Contains("500", result.Error);
        Assert.Contains("instance offline", result.Error);
    }

    [Fact]
    public async Task Un_fallo_de_red_se_traduce_a_failure()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}")
        {
            ThrowOnSend = new HttpRequestException("connection refused"),
        };

        var client = BuildClient(handler);

        var result = await client.SendAsync(OutboundMessage.Create(
            new Recipient(Guid.CreateVersion7(), "3000000000"), "Hola", Channel.WhatsApp));

        Assert.False(result.IsSuccess);
        Assert.Contains("connection refused", result.Error);
    }

    /// <summary>Handler falso que captura la solicitud y permite simular respuestas/errores.</summary>
    private sealed class FakeHttpHandler(HttpStatusCode status, string responseBody)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public Exception? ThrowOnSend { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (ThrowOnSend is not null)
            {
                throw ThrowOnSend;
            }

            Requests.Add(request);

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
