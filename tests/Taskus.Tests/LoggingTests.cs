using System.Text.Json;
using FluentAssertions;
using Serilog.Events;

namespace Taskus.Tests;

public class LoggingTests
{
    private static readonly DateTime Started = new(2026, 10, 27, 10, 15, 0);

    // The appsettings.json that ships next to taskus.exe; the build copies it to the test output.
    private static readonly string ShippedSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Priority", "High")]
    public void ShippedSettings_WithDebugLevel_WriteTextAndJsonFilePerRun()
    {
        using var temp = new TempFolder();

        using (var log = Logging.Create(ShippedSettings, temp.Path, Started, new SecretMask(), LogEventLevel.Debug))
        {
            log.Debug("detail {Row}", 7);
            log.Information("task {Task} done", "import");
        }

        var text = File.ReadAllText(temp.File("taskus_20261027_101500.log"));
        text.Should().Contain("[DBG] detail 7").And.Contain("[INF] task import done");

        var events = File.ReadAllLines(temp.File("taskus_20261027_101500.json")).Select(line => JsonDocument.Parse(line).RootElement).ToList();
        events.Should().HaveCount(2);
        events[1].GetProperty("MessageTemplate").GetString().Should().Be("task {Task} done");
        events[1].GetProperty("Properties").GetProperty("Task").GetString().Should().Be("import");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AbsolutePath_IsKept()
    {
        using var temp = new TempFolder();
        var target = temp.File("elsewhere", "app_{run}.log");
        var settings = temp.Write("appsettings.json",
            JsonSerializer.Serialize(new { Serilog = new { WriteTo = new[] { new { Name = "File", Args = new { path = target } } } } }));

        using (var log = Logging.Create(settings, temp.File("logs"), Started, new SecretMask()))
            log.Information("hello");

        File.ReadAllText(temp.File("elsewhere", "app_20261027_101500.log")).Should().Contain("hello");
        Directory.Exists(temp.File("logs")).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MissingSettings_FallsBackToTextFile()
    {
        using var temp = new TempFolder();

        using (var log = Logging.Create(temp.File("none.json"), temp.Path, Started, new SecretMask()))
            log.Information("hello");

        File.ReadAllText(temp.File("taskus_20261027_101500.log")).Should().Contain("hello");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ShippedSettings_DefaultLevel_LeavesOutDebug()
    {
        using var temp = new TempFolder();

        using (var log = Logging.Create(ShippedSettings, temp.Path, Started, new SecretMask()))
        {
            log.Debug("detail");
            log.Information("summary");
        }

        File.ReadAllText(temp.File("taskus_20261027_101500.log")).Should().Contain("summary").And.NotContain("detail");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void TraceLevel_WritesVerboseToFiles()
    {
        using var temp = new TempFolder();

        using (var log = Logging.Create(ShippedSettings, temp.Path, Started, new SecretMask(), LogEventLevel.Verbose))
            log.Verbose("row {Row} read", 3);

        File.ReadAllText(temp.File("taskus_20261027_101500.log")).Should().Contain("[VRB] row 3 read");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Priority", "High")]
    public void SecretInProperty_IsMaskedInBothFiles()
    {
        using var temp = new TempFolder();
        var mask = new SecretMask();
        mask.Add(new Dictionary<string, object?> { ["api_token"] = "s3cr3t-t0ken" });

        using (var log = Logging.Create(ShippedSettings, temp.Path, Started, mask))
            log.Warning("{Url}: failed", "http://example.com/api?key=s3cr3t-t0ken");

        foreach (var file in Directory.GetFiles(temp.Path))
            File.ReadAllText(file).Should().Contain("key=***").And.NotContain("s3cr3t-t0ken", file);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("trace", LogEventLevel.Verbose)]
    [InlineData("DEBUG", LogEventLevel.Debug)]
    [InlineData("warning", LogEventLevel.Warning)]
    public void TryParseLevel_AcceptsSerilogNamesAndTrace(string text, LogEventLevel expected)
    {
        Logging.TryParseLevel(text, out var level).Should().BeTrue();
        level.Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryParseLevel_RejectsUnknown()
    {
        Logging.TryParseLevel("loud", out _).Should().BeFalse();
    }
}
