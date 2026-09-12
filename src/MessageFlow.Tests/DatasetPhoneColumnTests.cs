using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Tests;

/// <summary>
/// Columna telefónica del dataset (§7): selección obligatoria al cargar, validación de
/// existencia con la estrategia de comparación del proyecto y cambio posterior.
/// </summary>
public class DatasetPhoneColumnTests
{
    private static Dataset CreateDataset()
    {
        var dataset = Dataset.Create("Contactos", "contactos.xlsx", "owner-1");
        dataset.AddColumn("Nombre", ColumnDataType.Text, ordinal: 0);
        dataset.AddColumn("Numero Celular", ColumnDataType.Text, ordinal: 1);
        dataset.AddColumn("Numero Auxiliar", ColumnDataType.Text, ordinal: 2);
        return dataset;
    }

    [Fact]
    public void Acepta_una_columna_existente_por_nombre_exacto()
    {
        var dataset = CreateDataset();

        dataset.SetPhoneColumn("Numero Celular");

        Assert.Equal("Numero Celular", dataset.PhoneColumn);
    }

    [Fact]
    public void La_comparacion_es_insensible_a_mayusculas()
    {
        var dataset = CreateDataset();

        dataset.SetPhoneColumn("NUMERO celular");

        Assert.Equal("Numero Celular", dataset.PhoneColumn);
    }

    [Fact]
    public void Normaliza_espacios_internos_y_extremos_antes_de_comparar()
    {
        var dataset = CreateDataset();

        dataset.SetPhoneColumn("  numero   celular  ");

        Assert.Equal("Numero Celular", dataset.PhoneColumn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Un_valor_vacio_o_blanco_es_rechazado(string? phoneColumn)
    {
        var dataset = CreateDataset();

        Assert.Throws<InvalidDatasetFileException>(() => dataset.SetPhoneColumn(phoneColumn));
    }

    [Fact]
    public void Una_columna_inexistente_lanza_error_y_no_modifica_el_valor_actual()
    {
        var dataset = CreateDataset();
        dataset.SetPhoneColumn("Numero Celular");

        Assert.Throws<UnknownColumnException>(() => dataset.SetPhoneColumn("Telefono Personal"));

        Assert.Equal("Numero Celular", dataset.PhoneColumn); // valor anterior intacto
    }

    [Fact]
    public void Se_puede_cambiar_la_columna_despues_de_crear_el_dataset()
    {
        var dataset = CreateDataset();
        dataset.SetPhoneColumn("Numero Celular");

        dataset.SetPhoneColumn("Numero Auxiliar");

        Assert.Equal("Numero Auxiliar", dataset.PhoneColumn);
    }
}
