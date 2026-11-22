using MessageFlow.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageFlow.Tests;

/// <summary>Reintento y respaldo automático entre proveedores de IA.</summary>
public class FallbackLlmClientTests
{
    private static FallbackLlmClient Build(ILlmClient primary, ILlmClient? secondary)
        => new(primary, secondary, NullLogger<FallbackLlmClient>.Instance, retryDelay: TimeSpan.Zero);

    private static LlmProviderException Failure(LlmFailureKind kind, TimeSpan? retryAfter = null)
        => ScriptedLlmClient.Failure(kind, retryAfter);

    [Fact]
    public async Task Si_el_primario_responde_no_toca_al_secundario()
    {
        var primary = new ScriptedLlmClient("Gemini", "ok-primary");
        var secondary = new ScriptedLlmClient("OpenAI", "ok-secondary");

        var text = await Build(primary, secondary).CompleteAsync("s", "p");

        Assert.Equal("ok-primary", text);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, secondary.Calls);
    }

    [Fact]
    public async Task Un_fallo_transitorio_se_reintenta_una_vez_en_el_mismo_proveedor()
    {
        var primary = new ScriptedLlmClient("Gemini", Failure(LlmFailureKind.Transient), "ok-after-retry");
        var secondary = new ScriptedLlmClient("OpenAI", "ok-secondary");

        var text = await Build(primary, secondary).CompleteAsync("s", "p");

        Assert.Equal("ok-after-retry", text);
        Assert.Equal(2, primary.Calls);
        Assert.Equal(0, secondary.Calls);
    }

    [Fact]
    public async Task Si_el_reintento_tambien_falla_cae_al_secundario()
    {
        var primary = new ScriptedLlmClient("Gemini", Failure(LlmFailureKind.Transient), Failure(LlmFailureKind.Transient));
        var secondary = new ScriptedLlmClient("OpenAI", "ok-secondary");

        var text = await Build(primary, secondary).CompleteAsync("s", "p");

        Assert.Equal("ok-secondary", text);
        Assert.Equal(2, primary.Calls);
        Assert.Equal(1, secondary.Calls);
    }

    [Fact]
    public async Task Un_rechazo_del_proveedor_cae_al_secundario_sin_reintentar()
    {
        var primary = new ScriptedLlmClient("Gemini", Failure(LlmFailureKind.ProviderRejected));
        var secondary = new ScriptedLlmClient("OpenAI", "ok-secondary");

        var text = await Build(primary, secondary).CompleteAsync("s", "p");

        Assert.Equal("ok-secondary", text);
        Assert.Equal(1, primary.Calls);
    }

    [Fact]
    public async Task Una_peticion_invalida_no_cae_al_secundario()
    {
        var primary = new ScriptedLlmClient("Gemini", Failure(LlmFailureKind.RequestFailed));
        var secondary = new ScriptedLlmClient("OpenAI", "ok-secondary");

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => Build(primary, secondary).CompleteAsync("s", "p"));

        Assert.Equal(LlmFailureKind.RequestFailed, ex.Kind);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, secondary.Calls);
    }

    [Fact]
    public async Task Sin_secundario_propaga_el_error_original_tras_un_reintento()
    {
        var original = Failure(LlmFailureKind.Transient);
        var primary = new ScriptedLlmClient("Gemini", Failure(LlmFailureKind.Transient), original);

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => Build(primary, null).CompleteAsync("s", "p"));

        Assert.Same(original, ex);
        Assert.Equal(2, primary.Calls);
    }

    [Fact]
    public async Task Si_ambos_fallan_devuelve_un_mensaje_generico_sin_detalles_de_proveedor()
    {
        var primary = new ScriptedLlmClient("Gemini", Failure(LlmFailureKind.Transient), Failure(LlmFailureKind.Transient));
        var secondary = new ScriptedLlmClient("OpenAI", Failure(LlmFailureKind.Transient), Failure(LlmFailureKind.ProviderRejected));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => Build(primary, secondary).CompleteAsync("s", "p"));

        Assert.Contains("temporarily unavailable", ex.Message);
        Assert.DoesNotContain("Gemini", ex.Message);
        Assert.DoesNotContain("OpenAI", ex.Message);
    }

    [Fact]
    public async Task Respeta_Retry_After_corto_y_no_espera_mas_de_cinco_segundos()
    {
        // Con Retry-After enorme el cliente no se bloquea: usa su propio retardo (0 en el test) en lugar de esperar.
        var primary = new ScriptedLlmClient(
            "Gemini",
            Failure(LlmFailureKind.Transient, TimeSpan.FromMinutes(10)),
            "ok");

        var started = DateTime.UtcNow;
        var text = await Build(primary, null).CompleteAsync("s", "p");

        Assert.Equal("ok", text);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task La_cancelacion_del_llamante_no_dispara_el_respaldo()
    {
        var primary = new ScriptedLlmClient("Gemini", new OperationCanceledException());
        var secondary = new ScriptedLlmClient("OpenAI", "ok-secondary");

        await Assert.ThrowsAsync<OperationCanceledException>(() => Build(primary, secondary).CompleteAsync("s", "p"));

        Assert.Equal(0, secondary.Calls);
    }
}
