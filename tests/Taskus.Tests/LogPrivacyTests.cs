using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using MiniExcelLibs;
using NSubstitute;
using Taskus.Config;
using Taskus.Mapping;
using Taskus.Pipeline;

namespace Taskus.Tests;

public class LogPrivacyTests
{
    private const string Token = "tok-9f8e7d6c";

    private static int ClosedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port; // nothing listens once the probe stops
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Priority", "High")]
    public async Task SecretInUrl_NeverReachesLogNorReport()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.File("in"));
        MiniExcel.SaveAs(temp.File("in", "ids.xlsx"), new[] { new Dictionary<string, object?> { ["Id"] = 1 } });
        var secrets = temp.Write("secrets.yaml", $"api_token: {Token}\n");
        var task = temp.Write("task.yaml", $$$"""
            source: { type: excel, folder: {{{temp.File("in")}}} }
            sink:
              type: http
              url: "http://127.0.0.1:{{{ClosedPort()}}}/api?key=${secret:api_token}&id={{ row.Id }}"
              retry: { attempts: 1, delay: 0 }
            onError: continue
            report: { folder: {{{temp.File("reports")}}}, when: always }
            """);
        var sink = new CollectingSink();
        var mask = new SecretMask();

        var exit = await new App(sink.Logger(mask), mask).ExecuteAsync("run", task, secrets, dryRun: false, TestContext.Current.CancellationToken);

        exit.Should().Be(ExitCode.Errors);
        sink.Text.Should().Contain("key=***").And.NotContain(Token);
        var report = Directory.GetFiles(temp.File("reports")).Single();
        var error = MiniExcel.Query(report, useHeaderRow: true, sheetName: "Errors").Cast<IDictionary<string, object?>>().Single();
        ((string)error["Message"]!).Should().Contain("key=***").And.NotContain(Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public async Task LogExclude_HidesFieldValuesInRowLogs()
    {
        var source = Substitute.For<ISource>();
        var rows = new[] { new SourceRow(2, new Dictionary<string, object?> { ["Email"] = "ann@example.com", ["Id"] = 7 }) };
        source.Units().Returns([new SourceUnit("a", rows.ToAsyncEnumerable(), _ => { })]);
        var mapper = RowMapper.Create([KeyValuePair.Create("email", "{{ row.Email }}"), KeyValuePair.Create("id", "{{ row.Id }}")]).Value;
        var sink = new CollectingSink();
        var runner = new Runner("t", source, mapper, Substitute.For<ISink>(), ErrorPolicy.Stop, 100, sink.Logger(), ["email"]);

        await runner.RunAsync(dryRun: true, TestContext.Current.CancellationToken);

        sink.Events.Should().Contain(e => e.MessageTemplate.Text.Contains("read:"), "trace logs every source row")
            .And.Contain(e => e.MessageTemplate.Text.Contains("dry run"));
        sink.Text.Should().NotContain("ann@example.com").And.Contain("7");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TaskLoader_ReadsLogExclude()
    {
        using var temp = new TempFolder();
        var file = temp.Write("t.yaml", "source: { type: excel, folder: x }\nsink: { type: excel, folder: y, file: z.xlsx }\nlogExclude: [email, personal_code]\n");

        TaskLoader.Load(new System.IO.Abstractions.FileSystem(), file, Secrets.None).Value.LogExclude.Should().Equal("email", "personal_code");
    }
}
