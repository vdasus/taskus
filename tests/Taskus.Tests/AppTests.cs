using FluentAssertions;
using MiniExcelLibs;
using Serilog.Core;

namespace Taskus.Tests;

public class AppTests
{
    private static void WriteInput(string file) =>
        MiniExcel.SaveAs(file, new[]
        {
            new Dictionary<string, object?> { ["Code"] = " A1 ", ["Limit"] = 10 },
            new Dictionary<string, object?> { ["Code"] = null, ["Limit"] = null },
            new Dictionary<string, object?> { ["Code"] = "B2", ["Limit"] = 20 },
        });

    private static string Task(TempFolder temp, string extra = "") => temp.Write("task.yaml", $$$"""
        source:
          type: excel
          folder: {{{temp.File("in")}}}
          processed: {{{temp.File("done")}}}
          failed: {{{temp.File("failed")}}}
        map:
          code: "{{ row.Code | string.strip }}"
          limit: "{{ row.Limit - 1 }}"
        sink:
          type: excel
          folder: {{{temp.File("out")}}}
          file: "{{ unit }}_out.xlsx"
        report:
          folder: {{{temp.File("reports")}}}
          when: always
        {{{extra}}}
        """);

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Priority", "High")]
    public async Task Run_ExcelToExcel_WritesMappedRowsAndMovesInput()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.File("in"));
        WriteInput(temp.File("in", "clients.xlsx"));

        var exit = await new App(Logger.None).ExecuteAsync("run", Task(temp), "none.yaml", dryRun: false, TestContext.Current.CancellationToken);

        exit.Should().Be(ExitCode.Success);
        var rows = MiniExcel.Query(temp.File("out", "clients_out.xlsx"), useHeaderRow: true)
            .Cast<IDictionary<string, object?>>()
            .Select(r => (Convert.ToString(r["code"]), Convert.ToDouble(r["limit"])))
            .ToList();
        rows.Should().Equal(("A1", 9.0), ("B2", 19.0));
        File.Exists(temp.File("done", "clients.xlsx")).Should().BeTrue();
        File.Exists(temp.File("in", "clients.xlsx")).Should().BeFalse();
        Directory.GetFiles(temp.File("reports"), "*.xlsx").Should().ContainSingle();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_DryRun_WritesNothingAndKeepsInput()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.File("in"));
        WriteInput(temp.File("in", "clients.xlsx"));

        var exit = await new App(Logger.None).ExecuteAsync("run", Task(temp), "none.yaml", dryRun: true, TestContext.Current.CancellationToken);

        exit.Should().Be(ExitCode.Success);
        Directory.Exists(temp.File("out")).Should().BeFalse();
        File.Exists(temp.File("in", "clients.xlsx")).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_EmptyFolder_SucceedsWithoutOutput()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.File("in"));

        var exit = await new App(Logger.None).ExecuteAsync("run", Task(temp), "none.yaml", dryRun: false, TestContext.Current.CancellationToken);

        exit.Should().Be(ExitCode.Success);
        Directory.Exists(temp.File("out")).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_RowErrorWithStop_MovesInputToFailed()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.File("in"));
        MiniExcel.SaveAs(temp.File("in", "bad.xlsx"), new[] { new Dictionary<string, object?> { ["Code"] = "A", ["Limit"] = "x" } });

        var exit = await new App(Logger.None).ExecuteAsync("run", Task(temp), "none.yaml", dryRun: false, TestContext.Current.CancellationToken);

        exit.Should().Be(ExitCode.Errors);
        File.Exists(temp.File("failed", "bad.xlsx")).Should().BeTrue();
        File.Exists(temp.File("out", "bad_out.xlsx")).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Validate_UnknownSinkType_IsInvalid()
    {
        using var temp = new TempFolder();
        var file = temp.Write("t.yaml", "source: { type: excel, folder: x }\nsink: { type: ftp }\n");

        var exit = await new App(Logger.None).ExecuteAsync("validate", file, "none.yaml", false, TestContext.Current.CancellationToken);

        exit.Should().Be(ExitCode.Invalid);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Validate_GoodTask_IsValidWithoutTouchingFiles()
    {
        using var temp = new TempFolder();

        var exit = await new App(Logger.None).ExecuteAsync("validate", Task(temp), "none.yaml", false, TestContext.Current.CancellationToken);

        exit.Should().Be(ExitCode.Success);
    }
}
