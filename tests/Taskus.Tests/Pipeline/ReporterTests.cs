using FluentAssertions;
using MiniExcelLibs;
using Taskus.Config;
using Taskus.Pipeline;

namespace Taskus.Tests.Pipeline;

public class ReporterTests
{
    private static RunReport Report()
    {
        var report = new RunReport("import") { Finished = DateTime.Now };
        var unit = new UnitReport("a.xlsx") { Read = 3, Written = 2, Completed = true };
        unit.Errors.Add(new RowError(4, "bad code"));
        report.Units.Add(unit);
        return report;
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(ReportWhen.Always, false, true)]
    [InlineData(ReportWhen.OnError, false, false)]
    [InlineData(ReportWhen.OnError, true, true)]
    [InlineData(ReportWhen.Never, true, false)]
    public void ShouldReport_FollowsWhen(ReportWhen when, bool withErrors, bool expected)
    {
        var report = new RunReport("t");
        report.Units.Add(new UnitReport("u") { Completed = !withErrors });

        Reporter.ShouldReport(when, report).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Text_ListsUnitsAndRowErrors()
    {
        var text = Reporter.Text(Report());

        text.Should().Contain("Result: errors")
            .And.Contain("a.xlsx: completed, read 3, written 2, errors 1")
            .And.Contain("row 4: bad code");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Save_WritesSummaryAndErrorSheets()
    {
        using var temp = new TempFolder();

        var file = Reporter.Save(temp.Path, Report());

        MiniExcel.GetSheetNames(file).Should().Equal("Summary", "Errors");
        var error = MiniExcel.Query(file, useHeaderRow: true, sheetName: "Errors").Cast<IDictionary<string, object?>>().Single();
        error["Message"].Should().Be("bad code");
    }
}
