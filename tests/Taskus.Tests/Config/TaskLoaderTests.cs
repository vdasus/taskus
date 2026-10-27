using System.IO.Abstractions.TestingHelpers;
using System.Text.Json.Nodes;
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
          transaction: all
        onError: continue
        maxErrors: 5
        report:
          folder: C:\reports
          mailTo: me@example.com
          when: always
        smtp: ${secret:smtp}
        logExclude: [email]
        """;

    private static readonly JsonObject SecretValues = new()
    {
        ["pg"] = "Host=db",
        ["smtp"] = new JsonObject { ["server"] = "mail.local", ["port"] = "25", ["from"] = "t@example.com" },
    };

    private static Result<TaskOptions> Load(string yaml, JsonObject? secrets = null, string file = "t.yaml") =>
        TaskLoader.Load(new MockFileSystem(new Dictionary<string, MockFileData> { [file] = new(yaml) }), file, secrets ?? Secrets.None);

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public void Load_FullTask_ReadsEverySection()
    {
        var result = Load(FullTask, SecretValues);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        var task = result.Value;
        task.Name.Should().Be("import-clients");
        task.Source.Should().Be(new ExcelSourceOptions { Folder = @"C:\in" });
        task.Sink.Should().Be(new PostgresSinkOptions { Connection = "Host=db", Table = "public.clients", Transaction = TransactionScope.All });
        task.Map.Should().Equal(new Dictionary<string, string> { ["code"] = "{{ row.Code }}", ["name"] = "{{ row.Name }}" });
        task.OnError.Should().Be(ErrorPolicy.Continue);
        task.MaxErrors.Should().Be(5);
        task.Report.Should().Be(new ReportOptions { Folder = @"C:\reports", MailTo = "me@example.com", When = ReportWhen.Always });
        task.Smtp.Should().Be(new SmtpOptions { Server = "mail.local", Port = 25, From = "t@example.com" });
        task.LogExclude.Should().Equal("email");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_MinimalTask_UsesDefaults()
    {
        var task = Load("source: { type: excel, folder: x }\nsink: { type: excel, folder: y, file: z.xlsx }\n", file: "minimal.yaml").Value;

        task.Name.Should().Be("minimal");
        task.OnError.Should().Be(ErrorPolicy.Stop);
        task.MaxErrors.Should().Be(100);
        task.Map.Should().BeEmpty();
        task.Report.Should().Be(new ReportOptions());
        task.Source.As<ExcelSourceOptions>().Pattern.Should().Be("*.xlsx");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_HttpSource_ReadsMethodCaseInsensitivelyAndNumbersFromText()
    {
        var result = Load("""
            source: { type: HTTP, method: post, url: "http://x/api", body: "{}", timeout: 2.5, retry: { attempts: 1 } }
            sink: { type: excel, folder: y, file: z.xlsx }
            """);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        var source = result.Value.Source.As<HttpSourceOptions>();
        source.Method.Should().Be(HttpMethodName.Post);
        source.Timeout.Should().Be(2.5);
        source.Retry.Should().Be(new RetryOptions { Attempts = 1, Delay = 1 });
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    [InlineData("source: { type: excel, folder: x, procesed: y }\nsink: { type: excel, folder: y, file: z }\n", "source.procesed: unknown setting")]
    [InlineData("source: { type: excel, folder: x }\nsink: { type: excel, folder: y }\n", "sink: missing required setting 'file'")]
    [InlineData("source: { type: ftp }\nsink: { type: excel, folder: y, file: z }\n", "source.type: unknown type 'ftp' (excel | postgres | http)")]
    [InlineData("source: { folder: x }\nsink: { type: excel, folder: y, file: z }\n", "source.type: is required")]
    [InlineData("source: { type: excel, folder: x }\nsink: { type: excel, folder: y, file: z }\nmaxErrors: many\n", "maxErrors: must be a whole number")]
    [InlineData("source: { type: excel, folder: x }\nsink: { type: excel, folder: y, file: z }\nonError: maybe\n", "onError: must be stop | continue")]
    [InlineData("source: { type: excel, folder: x }\n", "missing required setting 'sink'")]
    [InlineData("source: { type: excel, folder: x }\nsink: { type: http, method: TRACE, url: http://x }\n", "sink.method: must be GET | POST | PUT | PATCH | DELETE")]
    [InlineData("name: a/b\nsource: { type: excel, folder: x }\nsink: { type: excel, folder: y, file: z }\n", "name: must not contain characters")]
    [InlineData("- a\n- b\n", "must be a mapping")]
    public void Load_BadFile_ExplainsTheProblem(string yaml, string error)
    {
        var result = Load(yaml);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain(error);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_InvalidValues_ReportsEveryRule()
    {
        var result = Load("""
            source: { type: http, method: GET, url: "{{ 1 + }}", body: "{}", timeout: 0 }
            sink: { type: smtp }
            maxErrors: -1
            report: { mailTo: not-an-address }
            """);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("source.url: ")
            .And.Contain("source.timeout: must be positive")
            .And.Contain("source.body: needs method POST, PUT or PATCH")
            .And.Contain("maxErrors: must not be negative")
            .And.Contain("smtp: is required by the smtp sink")
            .And.Contain("smtp: is required by report.mailTo")
            .And.Contain("report.mailTo: must be e-mail addresses");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_BrokenYaml_FailsWithFileName()
    {
        var result = Load("source: [unclosed\n", file: "broken.yaml");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("broken.yaml");
    }
}
