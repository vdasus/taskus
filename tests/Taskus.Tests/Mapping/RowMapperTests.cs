using FluentAssertions;
using Taskus.Mapping;

namespace Taskus.Tests.Mapping;

public class RowMapperTests
{
    private static RowMapper Mapper(params (string Field, string Template)[] map) =>
        RowMapper.Create(map.Select(m => KeyValuePair.Create(m.Field, m.Template)).ToList()).Value;

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public void Map_SingleExpression_KeepsValueType()
    {
        var mapper = Mapper(("limit", "{{ row.Limit }}"), ("missing", "{{ row.Other ?? 0 }}"));

        var row = mapper.Map(new Dictionary<string, object?> { ["Limit"] = 12.5 }).Value;

        row["limit"].Should().Be(12.5);
        row["missing"].Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Map_TextTemplate_RendersString()
    {
        var mapper = Mapper(
            ("subject", "Invoice {{ row.Id }}"),
            ("name", "{{ row['Client name'] | string.strip }}"));

        var row = mapper.Map(new Dictionary<string, object?> { ["Id"] = 7, ["Client name"] = "  Ann  " }).Value;

        row["subject"].Should().Be("Invoice 7");
        row["name"].Should().Be("Ann");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Map_MultiLineBody_RendersEveryLine()
    {
        var mapper = Mapper(("body", "Hello {{ row.name }},\namount {{ row.amount | math.format \"0.00\" }}\n"));

        var row = mapper.Map(new Dictionary<string, object?> { ["name"] = "Ann", ["amount"] = 3m }).Value;

        row["body"].Should().Be("Hello Ann,\namount 3.00\n");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Map_EmptyMap_PassesRowThrough()
    {
        var source = new Dictionary<string, object?> { ["a"] = 1 };

        var row = RowMapper.Create([]).Value.Map(source).Value;

        row.Should().BeSameAs(source);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_InvalidTemplate_FailsWithFieldName()
    {
        var result = RowMapper.Create([KeyValuePair.Create("code", "{{ row.Code | }}")]);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("map.code");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Map_TextInArithmetic_FailsTheRow()
    {
        var mapper = Mapper(("x", "{{ row.a - 1 }}"));

        var result = mapper.Map(new Dictionary<string, object?> { ["a"] = "text" });

        result.IsFailure.Should().BeTrue();
        result.Error.Should().StartWith("map.x");
    }
}
