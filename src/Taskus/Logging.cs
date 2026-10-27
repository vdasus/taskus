using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Settings.Configuration;

namespace Taskus;

/// <summary>Builds the logger from the <c>Serilog</c> section of <c>appsettings.json</c>.</summary>
public static class Logging
{
    /// <remarks>
    /// In every sink <c>path</c>, <c>{run}</c> becomes the run start time, so each run gets its own files.
    /// A relative path resolves against <paramref name="logFolder"/>. Without the settings file the logger writes
    /// to the console and to one text file per run.
    /// </remarks>
    public static Logger Create(string settingsFile, string logFolder, DateTime started)
    {
        var run = started.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        if (!File.Exists(settingsFile))
        {
            return new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
                .WriteTo.File(Path.Combine(logFolder, $"taskus_{run}.log"))
                .CreateLogger();
        }

        var builder = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(settingsFile), optional: false);
        var paths = builder.Build().AsEnumerable()
            .Where(kv => kv.Key.EndsWith(":Args:path", StringComparison.OrdinalIgnoreCase) && kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => (string?)Path.Combine(logFolder, kv.Value!.Replace("{run}", run)));
        var configuration = builder.AddInMemoryCollection(paths).Build();

        // Sink assemblies are named here: a single-file executable has no assembly files to scan.
        var options = new ConfigurationReaderOptions(
            typeof(ConsoleLoggerConfigurationExtensions).Assembly,
            typeof(FileLoggerConfigurationExtensions).Assembly);
        return new LoggerConfiguration().ReadFrom.Configuration(configuration, options).CreateLogger();
    }
}
