using FluentAssertions;
using Taskus.Config;

namespace Taskus.Tests.Config;

public class ConnectorsTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public void Types_AreListedOnceInSchemaOrder()
    {
        Connectors.SourceTypes.Should().Equal("excel", "postgres", "odbc", "http");
        Connectors.SinkTypes.Should().Equal("excel", "postgres", "odbc", "smtp", "http");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EveryPart_ValidatesItsOwnOptionsType()
    {
        var parts = Connectors.All.Where(c => c.Source is not null).Select(c => (c.Source!.OptionsType, c.Source.Validator, Base: typeof(SourceOptions)))
            .Concat(Connectors.All.Where(c => c.Sink is not null).Select(c => (c.Sink!.OptionsType, c.Sink.Validator, Base: typeof(SinkOptions))))
            .ToList();

        parts.Should().HaveCount(9);
        parts.Should().AllSatisfy(p =>
        {
            p.OptionsType.Should().BeAssignableTo(p.Base);
            p.Validator.CanValidateInstancesOfType(p.OptionsType).Should().BeTrue(p.OptionsType.Name);
        });
        parts.Select(p => p.OptionsType).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OptionsType_FindsTheRecordByTypeName()
    {
        Connectors.SourceOptionsType("excel").Should().Be<ExcelSourceOptions>();
        Connectors.SinkOptionsType("smtp").Should().Be<SmtpSinkOptions>();
        FluentActions.Invoking(() => Connectors.SourceOptionsType("smtp")).Should().Throw<KeyNotFoundException>();
    }
}
