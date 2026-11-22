using MessageFlow.Infrastructure.AI;
using MessageFlow.Application.Abstractions;
using MessageFlow.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MessageFlow.Tests;

/// <summary>Configuración de proveedores de IA: nombres válidos, respaldo distinto y claves de los proveedores usados.</summary>
public class AiOptionsTests
{
    private static readonly GeminiOptions Gemini = new() { ApiKey = "g", Model = "gm" };
    private static readonly OpenAiOptions OpenAi = new() { ApiKey = "o", Model = "om" };

    [Fact]
    public void Por_defecto_usa_Gemini_sin_respaldo()
    {
        var options = new AiOptions();

        options.Validate();

        Assert.Equal(AiOptions.Gemini, options.NormalizedProvider);
        Assert.Null(options.NormalizedFallbackProvider);
    }

    [Theory]
    [InlineData("openai", "GEMINI")]
    [InlineData(" OpenAI ", "")]
    public void Acepta_nombres_sin_distinguir_mayusculas(string provider, string fallback)
    {
        var options = new AiOptions { Provider = provider, FallbackProvider = fallback };

        options.Validate();

        Assert.Equal(AiOptions.OpenAi, options.NormalizedProvider);
    }

    [Theory]
    [InlineData("Claude", "")]
    [InlineData("Gemini", "Mistral")]
    [InlineData("Gemini", "gemini")]
    public void Rechaza_proveedores_desconocidos_o_respaldo_igual_al_principal(string provider, string fallback)
    {
        var options = new AiOptions { Provider = provider, FallbackProvider = fallback };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Exige_clave_y_modelo_de_los_proveedores_que_se_usan()
    {
        var options = new AiOptions { Provider = "Gemini", FallbackProvider = "OpenAI" };

        options.ValidateProviders(Gemini, OpenAi);

        var noOpenAiKey = new OpenAiOptions { Model = "om" };
        var ex = Assert.Throws<InvalidOperationException>(() => options.ValidateProviders(Gemini, noOpenAiKey));
        Assert.Contains("OpenAI:ApiKey", ex.Message);
    }

    [Fact]
    public void No_exige_configurar_un_proveedor_que_no_se_usa()
    {
        // Solo OpenAI para texto: Gemini sin Model no impide el arranque (sus embeddings se validan aparte).
        var options = new AiOptions { Provider = "OpenAI" };

        options.ValidateProviders(new GeminiOptions { ApiKey = "g" }, OpenAi);
    }

    [Fact]
    public void La_seccion_enlaza_desde_configuracion()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "OpenAI",
                ["Ai:FallbackProvider"] = "Gemini",
                ["OpenAI:Model"] = "gpt-4.1-mini",
                ["OpenAI:BaseUrl"] = "https://example.test",
            })
            .Build();

        var ai = configuration.GetSection(AiOptions.SectionName).Get<AiOptions>()!;
        var openAi = configuration.GetSection(OpenAiOptions.SectionName).Get<OpenAiOptions>()!;

        Assert.Equal(AiOptions.OpenAi, ai.NormalizedProvider);
        Assert.Equal(AiOptions.Gemini, ai.NormalizedFallbackProvider);
        Assert.Equal("gpt-4.1-mini", openAi.Model);
        Assert.Equal("https://example.test", openAi.BaseUrl);
        Assert.Equal(1024, openAi.MaxOutputTokens);
    }

    [Theory]
    [InlineData("Gemini", "")]
    [InlineData("OpenAI", "Gemini")]
    [InlineData("Gemini", "OpenAI")]
    public void El_contenedor_resuelve_el_servicio_de_IA_con_cada_combinacion_de_proveedores(string provider, string fallback)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMessageFlowInfrastructure("Host=localhost;Database=unused");
        services.AddSingleton(new AiOptions { Provider = provider, FallbackProvider = fallback });
        services.AddSingleton(Gemini);
        services.AddSingleton(OpenAi);

        using var serviceProvider = services.BuildServiceProvider();

        Assert.NotNull(serviceProvider.GetRequiredService<IGenerativeAIService>());
        Assert.NotNull(serviceProvider.GetRequiredService<IEmbeddingService>());
    }
}
