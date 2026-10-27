using FluentAssertions;
using Taskus.Config;

namespace Taskus.Tests.Config;

public class TaskLoaderTests
{
    private const string FullTask = """
        name: import-clients
        source:
          type: excel
          folder: C:\in
        map:
          code: "{{ row.Code }}"
          name: "{{ row.Name }}"
        sink:
          type: postgres
          connection: ${secret:pg}
          table: public.clients
        onError: continue
        maxErrors: 5
        report:
          folder: C:\reports
          mailTo: me@example.com
          when: always
        smtp: ${secret:smtp}
        """;

    private static readonly Dictionary<string, object?> SecretValues = new()
    {
        ["pg"] = "Host=db",
        ["smtp"] = new Dictionary<object, object?> { ["server"] = "mail.local", ["from"] = "t@example.com" },
    };

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public void Load_FullTask_ReadsEverySection()
    {
        using var temp = new TempFolder();

        var result = TaskLoader.Load(temp.Write("t.yaml", FullTask), SecretValues);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        var task = result.Value;
        task.Name.Should().Be("import-clients");
        task.Source.Required("folder").Value.Should().Be(@"C:\in");
        task.Sink.Required("connection").Value.Should().Be("Host=db");
        task.Map.Select(kv => kv.Key).Should().Equal("code", "name");
        task.OnError.Should().Be(ErrorPolicy.Continue);
        task.MaxErrors.Should().Be(5);
        task.Report.Should().Be(new ReportOptions(@"C:\reports", "me@example.com", ReportWhen.Always));
        task.Smtp!.Required("server").Value.Should().Be("mail.local");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_MinimalTask_UsesDefaults()
    {
        using var temp = new TempFolder();
        var file = temp.Write("minimal.yaml", "source: { type: excel, folder: x }\nsink: { type: excel, folder: y, file: z.xlsx }\n");

        var task = TaskLoader.Load(file, Secrets.None).Value;

        task.Name.Should().Be("minimal");
        task.OnError.Should().Be(ErrorPolicy.Stop);
        task.MaxErrors.Should().Be(100);
        task.Map.Should().BeEmpty();
        task.Report.Should().Be(ReportOptions.Default);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_InvalidValues_ReportsEveryProblem()
    {
        using var temp = new TempFolder();
        var file = temp.Write("bad.yaml", "source: { type: excel }\nonError: maybe\nmaxErrors: many\n");

        var result = TaskLoader.Load(file, Secrets.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("sink is required")
            .And.Contain("onError 'maybe' must be one of: stop | continue")
            .And.Contain("maxErrors 'many' is not a whole number");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_MailToWithoutSmtp_Fails()
    {
        using var temp = new TempFolder();
        var file = temp.Write("t.yaml", "source: { type: excel, folder: x }\nsink: { type: excel }\nreport: { mailTo: a@b.c }\n");

        var result = TaskLoader.Load(file, Secrets.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("report.mailTo needs an smtp section");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_BrokenYaml_FailsWithFileName()
    {
        using var temp = new TempFolder();
        var file = temp.Write("broken.yaml", "source: [unclosed\n");

        var result = TaskLoader.Load(file, Secrets.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("broken.yaml");
    }
}
