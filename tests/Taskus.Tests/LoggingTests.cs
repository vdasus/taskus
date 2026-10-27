using System.Text.Json;
using FluentAssertions;

namespace Taskus.Tests;

public class LoggingTests
{
    private static readonly DateTime Started = new(2026, 10, 27, 10, 15, 0);

    // The appsettings.json that ships next to taskus.exe; the build copies it to the test output.
    private static readonly string ShippedSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Priority", "High")]
    public void ShippedSettings_WriteTextAndJsonFilePerRun()
    {
        using var temp = new TempFolder();

        using (var log = Logging.Create(ShippedSettings, temp.Path, Started))
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

        using (var log = Logging.Create(settings, temp.File("logs"), Started))
            log.Information("hello");

        File.ReadAllText(temp.File("elsewhere", "app_20261027_101500.log")).Should().Contain("hello");
        Directory.Exists(temp.File("logs")).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MissingSettings_FallsBackToTextFile()
    {
        using var temp = new TempFolder();

        using (var log = Logging.Create(temp.File("none.json"), temp.Path, Started))
            log.Information("hello");

        File.ReadAllText(temp.File("taskus_20261027_101500.log")).Should().Contain("hello");
    }
}
