using MessageFlow.Domain.Scheduling;

namespace MessageFlow.Tests;

public class DataTriggerMatcherTests
{
    private static readonly DateOnly ReferenceDate = new(2026, 1, 8);

    [Fact]
    public void Aniversario_coincide_aunque_el_anio_sea_distinto()
    {
        var matcher = DataTriggerMatcher.Create(
            DataTriggerConfig.Create(Guid.CreateVersion7(), Domain.Enums.DateTriggerMatchMode.AnniversaryDayMonth));

        Assert.True(matcher.Matches("1990-01-08", ReferenceDate));
        Assert.True(matcher.Matches("2005/01/08", ReferenceDate));
        Assert.True(matcher.Matches("08-01-1999", ReferenceDate));
    }

    [Theory]
    [InlineData("1990-01-09")] // otro día
    [InlineData("1990-02-08")] // otro mes
    [InlineData("1990-12-08")]
    public void Aniversario_no_coincide_si_varia_el_dia_o_el_mes(string cell)
    {
        var matcher = DataTriggerMatcher.Create(
            DataTriggerConfig.Create(Guid.CreateVersion7(), Domain.Enums.DateTriggerMatchMode.AnniversaryDayMonth));

        Assert.False(matcher.Matches(cell, ReferenceDate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no es una fecha")]
    public void Celda_invalida_o_vacia_nunca_coincide(string? cell)
    {
        var matcher = DataTriggerMatcher.Create(
            DataTriggerConfig.Create(Guid.CreateVersion7(), Domain.Enums.DateTriggerMatchMode.AnniversaryDayMonth));

        Assert.False(matcher.Matches(cell, ReferenceDate));
    }

    [Fact]
    public void El_cumpleanos_del_29_de_febrero_solo_coincide_en_anios_bisiestos()
    {
        // Regla documentada en el matcher: comparación estricta de día+mes.
        var matcher = DataTriggerMatcher.Create(
            DataTriggerConfig.Create(Guid.CreateVersion7(), Domain.Enums.DateTriggerMatchMode.AnniversaryDayMonth));

        Assert.True(matcher.Matches("2000-02-29", new DateOnly(2024, 2, 29)));  // bisiesto
        Assert.False(matcher.Matches("2000-02-29", new DateOnly(2026, 2, 28))); // no bisiesto
    }
}
