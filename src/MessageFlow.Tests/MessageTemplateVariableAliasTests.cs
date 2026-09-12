using MessageFlow.Application.Flows;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Domain.Messaging;

namespace MessageFlow.Tests;

/// <summary>
/// Variables de plantilla expresadas como Id de columna ({{guid}}): validación (§11),
/// normalización a nombre para render, y traducción de valores de muestra.
/// </summary>
public class MessageTemplateVariableAliasTests
{
    private static readonly Guid NombreId = Guid.CreateVersion7();
    private static readonly Guid CorreoId = Guid.CreateVersion7();

    private static readonly IReadOnlyList<ColumnDefinition> Columns =
    [
        new(NombreId, "Nombre", ColumnDataType.Text),
        new(CorreoId, "Correo", ColumnDataType.Text),
    ];

    [Fact]
    public void El_guard_acepta_variables_expresadas_por_id_de_columna()
    {
        var template = MessageTemplate.Create($"Hola {{{{{NombreId}}}}}, tu correo {{{{{CorreoId}}}}}");

        TemplateVariableGuard.EnsureSupported(template, Columns); // no lanza
    }

    [Fact]
    public void El_guard_acepta_formatos_mixtos_nombre_e_id()
    {
        var template = MessageTemplate.Create($"Hola {{{{{NombreId}}}}}, correo {{{{Correo}}}}");

        TemplateVariableGuard.EnsureSupported(template, Columns); // no lanza
    }

    [Fact]
    public void Un_id_que_no_corresponde_a_ninguna_columna_se_reporta_desconocido()
    {
        var template = MessageTemplate.Create($"Hola {{{{{Guid.CreateVersion7()}}}}}");

        Assert.Throws<UnknownTemplateVariablesException>(
            () => TemplateVariableGuard.EnsureSupported(template, Columns));
    }

    [Fact]
    public void Apply_normaliza_ids_a_nombres_y_conserva_los_tokens_por_nombre()
    {
        // Con espacios dentro de las llaves y mayúsculas alternadas en el Guid.
        // (En strings interpolados se requieren cuatro llaves para producir "{{".)
        var upperId = NombreId.ToString().ToUpperInvariant();

        var template = MessageTemplate.Create(
            $"Hola {{{{  {upperId}  }}}}, correo {{{{Correo}}}}");

        var normalized = TemplateColumnAliases.Apply(template, Columns);

        Assert.Equal("Hola {{Nombre}}, correo {{Correo}}", normalized.Text);

        // La plantilla original es inmutable: conserva el Id tal como lo envió el usuario.
        Assert.Contains(upperId, template.Text);
    }

    [Fact]
    public void Una_plantilla_sin_ids_de_columna_queda_intacta()
    {
        var template = MessageTemplate.Create("Hola {{Nombre}}, tu líder es {{Lider}}");

        Assert.Same(template, TemplateColumnAliases.Apply(template, Columns));
    }

    [Fact]
    public void TranslateSampleValues_resuelve_claves_por_id_y_conserva_las_por_nombre()
    {
        var sample = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [NombreId.ToString()] = "Juan",
            ["Correo"] = "juan@x.com",
        };

        var translated = TemplateColumnAliases.TranslateSampleValues(sample, Columns);

        Assert.Equal("Juan", translated["Nombre"]);
        Assert.Equal("Juan", translated[NombreId.ToString()]); // clave original conservada
        Assert.Equal("juan@x.com", translated["Correo"]);
    }

    [Fact]
    public void TranslateSampleValues_devuelve_la_misma_instancia_cuando_no_hay_ids()
    {
        var sample = new Dictionary<string, string?> { ["Nombre"] = "Juan" };

        Assert.Same(sample, TemplateColumnAliases.TranslateSampleValues(sample, Columns));
    }
}
