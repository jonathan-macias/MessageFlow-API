using MessageFlow.Domain.AI;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Infrastructure.AI;

namespace MessageFlow.Tests;

/// <summary>
/// Los prompts y la validación de la respuesta del modelo son comunes a todos los proveedores:
/// al extraerlos de GeminiService no deben aceptar nada que antes rechazaran.
/// </summary>
public class LlmGenerativeAIServiceTests
{
    private static readonly ColumnDefinition Name = new(Guid.NewGuid(), "Nombre", ColumnDataType.Text);
    private static readonly ColumnDefinition Amount = new(Guid.NewGuid(), "Monto", ColumnDataType.Number);
    private static readonly IReadOnlyList<ColumnDefinition> Columns = [Name, Amount];

    private static LlmGenerativeAIService Build(params object[] script)
        => new(new ScriptedLlmClient("Test", script));

    [Fact]
    public async Task Genera_el_mensaje_y_devuelve_solo_las_variables_usadas()
    {
        var json = "{\"messageTemplate\":\"Hola {{" + Name.Id + "}}\",\"variableIds\":[]}";

        var result = await Build(json).GenerateMessageAsync("saludo", "formal", Columns);

        Assert.Equal("Hola {{" + Name.Id + "}}", result.MessageTemplate);
        Assert.Equal([Name.Id], result.VariableIds);
    }

    [Fact]
    public async Task Rechaza_una_plantilla_con_variables_inexistentes()
    {
        var json = "{\"messageTemplate\":\"Hola {{" + Guid.NewGuid() + "}}\"}";

        var ex = await Assert.ThrowsAsync<DomainException>(() => Build(json).GenerateMessageAsync("x", "y", Columns));

        Assert.Contains("invalid variable IDs", ex.Message);
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("[1,2,3]")]
    public async Task Una_respuesta_que_no_es_un_objeto_json_es_un_error_controlado(string text)
    {
        await Assert.ThrowsAsync<DomainException>(() => Build(text).GenerateMessageAsync("x", "y", Columns));
        await Assert.ThrowsAsync<DomainException>(() => Build(text).GenerateDatasetQueryAsync("q", Columns));
    }

    [Fact]
    public async Task Interpreta_una_consulta_valida()
    {
        var json = """
            {"action":"sum","targetColumn":"Monto","groupByColumn":null,"limit":null,
             "filters":[{"column":"Nombre","operator":"equals","value":"Ana"}]}
            """;

        var query = await Build(json).GenerateDatasetQueryAsync("total de Ana", Columns);

        Assert.Equal(DatasetQueryAction.Sum, query.Action);
        Assert.Equal("Monto", query.TargetColumn);
        Assert.Single(query.Filters);
    }

    [Theory]
    [InlineData("""{"action":"delete"}""")]
    [InlineData("""{"action":"sum","targetColumn":"Inexistente"}""")]
    [InlineData("""{"action":"count","filters":[{"column":"Nombre","operator":"regex","value":"x"}]}""")]
    [InlineData("""{"action":"count","filters":[{"column":"Otra","operator":"equals","value":"x"}]}""")]
    public async Task Rechaza_acciones_columnas_y_operadores_invalidos(string json)
    {
        await Assert.ThrowsAsync<DomainException>(() => Build(json).GenerateDatasetQueryAsync("q", Columns));
    }

    [Fact]
    public async Task Las_respuestas_en_texto_pasan_tal_cual_y_los_errores_del_proveedor_se_propagan()
    {
        var service = Build("Hay 3 registros.", ScriptedLlmClient.Failure(LlmFailureKind.Transient));
        var query = DatasetQuery.Create(DatasetQueryAction.Count, null, [], null, null);

        Assert.Equal("Hay 3 registros.", await service.GenerateResponseAsync("¿cuántos?", query, new { count = 3 }));
        await Assert.ThrowsAsync<LlmProviderException>(() => service.GenerateResponseAsync("¿cuántos?", query, null));
    }

    [Fact]
    public async Task La_respuesta_fundamentada_exige_filas_y_envia_los_registros_en_el_prompt()
    {
        var client = new RecordingClient("Fila 7.");
        var service = new LlmGenerativeAIService(client);

        await Assert.ThrowsAsync<DomainException>(() => service.GenerateGroundedResponseAsync("q", [], []));

        var rows = new[] { new GroundedRow(7, 0.91, new Dictionary<string, string?> { ["Nombre"] = "Ana" }) };
        var answer = await service.GenerateGroundedResponseAsync("¿quién?", rows, []);

        Assert.Equal("Fila 7.", answer);
        Assert.Contains("Row 7", client.LastPrompt);
        Assert.Contains("Nombre: Ana", client.LastPrompt);
        Assert.Equal(LlmOutputFormat.Text, client.LastFormat); // respuesta en texto: sin modo JSON
    }

    private sealed class RecordingClient(string reply) : ILlmClient
    {
        public string Provider => "Test";

        public string LastPrompt { get; private set; } = string.Empty;

        public LlmOutputFormat LastFormat { get; private set; }

        public Task<string> CompleteAsync(string systemInstruction, string prompt, LlmOutputFormat format = LlmOutputFormat.Json, CancellationToken cancellationToken = default)
        {
            LastPrompt = prompt;
            LastFormat = format;
            return Task.FromResult(reply);
        }
    }
}
