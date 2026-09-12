using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Domain.Filters;

namespace MessageFlow.Tests;

public class FilterMatcherTests
{
    private static readonly Guid NameId = Guid.CreateVersion7();
    private static readonly Guid AgeId = Guid.CreateVersion7();
    private static readonly Guid BirthDateId = Guid.CreateVersion7();
    private static readonly Guid ActiveId = Guid.CreateVersion7();

    private static readonly IReadOnlyList<ColumnDefinition> Columns =
    [
        new(NameId, "Nombre", ColumnDataType.Text),
        new(AgeId, "Edad", ColumnDataType.Number),
        new(BirthDateId, "FechaNacimiento", ColumnDataType.Date),
        new(ActiveId, "Activo", ColumnDataType.Boolean),
    ];

    private static IReadOnlyDictionary<string, string?> Row(params (string Key, string? Value)[] cells)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in cells)
        {
            dict[key] = value;
        }

        return dict;
    }

    private static FilterCondition Cond(Guid columnId, FilterOperator op, string? value = null)
        => new(columnId, op, value);

    [Fact]
    public void Text_operators_are_case_insensitive()
    {
        var matcher = new FilterMatcher(
            FilterGroup.Create(FilterCompositionOperator.And,
            [
                Cond(NameId, FilterOperator.Contains, "princi"),
                Cond(NameId, FilterOperator.StartsWith, "principal"),
                Cond(NameId, FilterOperator.EndsWith, "PAL"),
                Cond(NameId, FilterOperator.Equals, "PRINCIPAL"),
                Cond(NameId, FilterOperator.NotEquals, "secundario"),
            ]),
            Columns);

        Assert.True(matcher.Matches(Row(("Nombre", "PRINCIPAL"))));
        Assert.False(matcher.Matches(Row(("Nombre", "Secundario"))));
    }

    [Fact]
    public void Number_comparisons_evaluate_invariantly_and_tolerate_bad_cells()
    {
        var matcher = new FilterMatcher(
            FilterGroup.Create(FilterCompositionOperator.And,
            [
                Cond(AgeId, FilterOperator.GreaterThanOrEqual, "18"),
                Cond(AgeId, FilterOperator.LessThan, "65"),
            ]),
            Columns);

        Assert.True(matcher.Matches(Row(("Edad", "30.5"))));
        Assert.False(matcher.Matches(Row(("Edad", "65"))));
        // Celda vacía o no numérica: la fila no coincide, sin excepción.
        Assert.False(matcher.Matches(Row(("Edad", ""))));
        Assert.False(matcher.Matches(Row(("Edad", null))));
        Assert.False(matcher.Matches(Row(("Edad", "treinta"))));
    }

    [Fact]
    public void Date_comparisons_use_shared_formats()
    {
        var matcher = new FilterMatcher(
            FilterGroup.Create(FilterCompositionOperator.And, [Cond(BirthDateId, FilterOperator.After, "1990-01-08")]),
            Columns);

        Assert.True(matcher.Matches(Row(("FechaNacimiento", "1995-12-31"))));
        Assert.True(matcher.Matches(Row(("FechaNacimiento", "31/12/1995".Replace('/', '-')))));
        Assert.False(matcher.Matches(Row(("FechaNacimiento", "1988-05-12"))));
        Assert.False(matcher.Matches(Row(("FechaNacimiento", "no-es-fecha"))));
    }

    [Fact]
    public void Boolean_equals_and_not_equals_work()
    {
        var matcher = new FilterMatcher(
            FilterGroup.Create(FilterCompositionOperator.And, [Cond(ActiveId, FilterOperator.Equals, "true")]),
            Columns);

        Assert.True(matcher.Matches(Row(("Activo", "true"))));
        Assert.False(matcher.Matches(Row(("Activo", "false"))));

        var notEquals = new FilterMatcher(
            FilterGroup.Create(FilterCompositionOperator.And, [Cond(ActiveId, FilterOperator.NotEquals, "true")]),
            Columns);

        Assert.True(notEquals.Matches(Row(("Activo", "false"))));
    }

    [Fact]
    public void Null_operators_cover_missing_and_blank_cells()
    {
        var isNull = new FilterMatcher(
            FilterGroup.Create(FilterCompositionOperator.And, [Cond(NameId, FilterOperator.IsNull)]),
            Columns);
        var isNotNull = new FilterMatcher(
            FilterGroup.Create(FilterCompositionOperator.And, [Cond(NameId, FilterOperator.IsNotNull)]),
            Columns);

        Assert.True(isNull.Matches(Row()));
        Assert.True(isNull.Matches(Row(("Nombre", "   "))));
        Assert.False(isNotNull.Matches(Row(("Nombre", ""))));
        Assert.True(isNotNull.Matches(Row(("Nombre", "Juan"))));
    }

    [Fact]
    public void And_requires_all_conditions_and_or_any()
    {
        var and = new FilterMatcher(
            FilterGroup.Create(FilterCompositionOperator.And,
            [
                Cond(NameId, FilterOperator.Equals, "Juan"),
                Cond(AgeId, FilterOperator.Equals, "30"),
            ]),
            Columns);

        var or = new FilterMatcher(
            FilterGroup.Create(FilterCompositionOperator.Or,
            [
                Cond(NameId, FilterOperator.Equals, "Juan"),
                Cond(AgeId, FilterOperator.Equals, "30"),
            ]),
            Columns);

        Assert.True(and.Matches(Row(("Nombre", "Juan"), ("Edad", "30"))));
        Assert.False(and.Matches(Row(("Nombre", "Juan"), ("Edad", "40"))));
        Assert.True(or.Matches(Row(("Nombre", "Juan"), ("Edad", "40"))));
        Assert.False(or.Matches(Row(("Nombre", "Pedro"), ("Edad", "40"))));
    }

    [Fact]
    public void Nested_groups_combine_recursively()
    {
        // (Nombre = Juan OR Nombre = Pedro) AND Edad >= 30
        var root = FilterGroup.Create(FilterCompositionOperator.And,
        [
            FilterGroup.Create(FilterCompositionOperator.Or,
            [
                Cond(NameId, FilterOperator.Equals, "Juan"),
                Cond(NameId, FilterOperator.Equals, "Pedro"),
            ]),
            Cond(AgeId, FilterOperator.GreaterThanOrEqual, "30"),
        ]);

        var matcher = new FilterMatcher(root, Columns);

        Assert.True(matcher.Matches(Row(("Nombre", "pedro"), ("Edad", "44"))));
        Assert.False(matcher.Matches(Row(("Nombre", "Maria"), ("Edad", "44"))));
        Assert.False(matcher.Matches(Row(("Nombre", "Juan"), ("Edad", "29"))));
    }

    [Fact]
    public void Unknown_column_is_rejected_at_construction()
    {
        Assert.Throws<DomainException>(() => new FilterMatcher(
            FilterGroup.Create(FilterCompositionOperator.And, [Cond(Guid.CreateVersion7(), FilterOperator.Equals, "x")]),
            Columns));
    }

    [Fact]
    public void Unsupported_operator_for_type_is_rejected()
    {
        Assert.Throws<UnsupportedFilterOperatorException>(() => new FilterMatcher(
            FilterGroup.Create(FilterCompositionOperator.And, [Cond(NameId, FilterOperator.GreaterThan, "1")]),
            Columns));
    }

    [Fact]
    public void Null_root_matches_everything()
    {
        var matcher = new FilterMatcher(null, Columns);

        Assert.True(matcher.Matches(Row()));
        Assert.True(matcher.Matches(Row(("Cualquiera", "valor"))));
    }
}
