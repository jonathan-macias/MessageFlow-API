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

    private static OutboundMessage MessageTo(string destination)
        => OutboundMessage.Create(new Recipient(Guid.CreateVersion7(), destination), "Hola", Channel.WhatsApp);

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Los_fallos_de_infraestructura_se_marcan_como_transitorios(HttpStatusCode status)
    {
        var client = BuildClient(new FakeHttpHandler(status, "{}"));

        var result = await client.SendAsync(MessageTo("3000000000"));

        Assert.False(result.IsSuccess);
        Assert.True(result.IsTransient);
    }

    [Fact]
    public async Task Un_rechazo_del_numero_es_un_fallo_permanente()
    {
        var client = BuildClient(new FakeHttpHandler(HttpStatusCode.BadRequest, """{"exists":false}"""));

        var result = await client.SendAsync(MessageTo("3000000000"));

        Assert.False(result.IsSuccess);
        Assert.False(result.IsTransient);
    }

    [Fact]
    public async Task Un_fallo_de_red_y_un_timeout_son_transitorios()
    {
        var network = BuildClient(new FakeHttpHandler(HttpStatusCode.OK, "{}")
        {
            ThrowOnSend = new HttpRequestException("connection refused"),
        });
        var timeout = BuildClient(new FakeHttpHandler(HttpStatusCode.OK, "{}")
        {
            ThrowOnSend = new TaskCanceledException("timeout"),
        });

        Assert.True((await network.SendAsync(MessageTo("3000000000"))).IsTransient);
        Assert.True((await timeout.SendAsync(MessageTo("3000000000"))).IsTransient);
    }

    [Theory]
    [InlineData("", "inst", "key")]
    [InlineData("https://evolution.test", "", "key")]
    [InlineData("https://evolution.test", "inst", "")]
    [InlineData("no es una url", "inst", "key")]
    public async Task Sin_configuracion_valida_no_lanza_y_devuelve_fallo_transitorio(
        string baseUrl, string instance, string apiKey)
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}");
        var client = new EvolutionApiClient(
            new HttpClient(handler),
            new EvolutionApiOptions { BaseUrl = baseUrl, Instance = instance, GlobalApiKey = apiKey },
            NullLogger<EvolutionApiClient>.Instance);

        var result = await client.SendAsync(MessageTo("3000000000"));

        Assert.False(result.IsSuccess);
        Assert.True(result.IsTransient);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Respuesta_2xx_con_cuerpo_ilegible_se_considera_enviada()
    {
        var client = BuildClient(new FakeHttpHandler(HttpStatusCode.OK, "esto no es json"));

        var result = await client.SendAsync(MessageTo("3000000000"));

        // Marcarlo como fallo provocaría un reenvío duplicado de un mensaje que probablemente salió.
        Assert.True(result.IsSuccess);
        Assert.Null(result.ProviderMessageId);
    }

    [Fact]
    public async Task Los_logs_no_contienen_el_telefono_completo()
    {
        var logger = new CapturingLogger();
        var httpClient = new HttpClient(
            new FakeHttpHandler(HttpStatusCode.BadRequest, """{"number":"573001234567","exists":false}"""))
        {
            BaseAddress = new Uri("https://evolution.test/"),
        };

        var client = new EvolutionApiClient(
            httpClient,
            new EvolutionApiOptions { BaseUrl = "https://evolution.test", Instance = Instance, GlobalApiKey = ApiKey },
            logger);

        await client.SendAsync(MessageTo("573001234567"));

        var logged = string.Join("\n", logger.Messages);
        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain("573001234567", logged);
        Assert.Contains("4567", logged);
    }

    [Theory]
    [InlineData("+573001234567", "+********4567")]
    [InlineData("3001234567", "******4567")]
    [InlineData("1234", "****")]
    [InlineData("", "")]
    public void PhoneMasker_deja_visibles_solo_los_ultimos_cuatro_digitos(string phone, string expected)
        => Assert.Equal(expected, PhoneMasker.Mask(phone));

    /// <summary>Logger que conserva los mensajes ya formateados.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<EvolutionApiClient>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
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
