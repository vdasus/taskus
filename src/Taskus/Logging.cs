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
    /// In every sink <c>path</c>, <c>{run}</c> becomes the run start time and the process id, so each run gets its own files.
    /// A relative path resolves against <paramref name="logFolder"/>. <paramref name="level"/>, when given,
    /// replaces the minimum level of the settings file. Without the settings file the logger writes to the
    /// console and to one text file per run. <paramref name="processId"/> is added to <c>{run}</c>: two runs started
    /// in the same second would otherwise share a file name, and the second one could not open it.
    /// </remarks>
    public static Logger Create(string settingsFile, string logFolder, DateTime started, SecretMask mask, LogEventLevel? level = null, int? processId = null)
    {
        var run = started.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + $"_{processId ?? Environment.ProcessId}";
        if (!File.Exists(settingsFile))
        {
            return new LoggerConfiguration()
                .MinimumLevel.Is(level ?? LogEventLevel.Information)
                .Enrich.With(mask)
                .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
                .WriteTo.File(Path.Combine(logFolder, $"taskus_{run}.log"))
                .CreateLogger();
        }

        var builder = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(settingsFile), optional: false);
        var overrides = builder.Build().AsEnumerable()
            .Where(kv => kv.Key.EndsWith(":Args:path", StringComparison.OrdinalIgnoreCase) && kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => (string?)Path.Combine(logFolder, kv.Value!.Replace("{run}", run)));
        if (level is not null)
            overrides["Serilog:MinimumLevel"] = level.ToString(); // wins over both "Debug" and { "Default": ... } forms
        var configuration = builder.AddInMemoryCollection(overrides).Build();

        // Sink assemblies are named here: a single-file executable has no assembly files to scan.
        var options = new ConfigurationReaderOptions(
            typeof(ConsoleLoggerConfigurationExtensions).Assembly,
            typeof(FileLoggerConfigurationExtensions).Assembly);
        return new LoggerConfiguration()
            .ReadFrom.Configuration(configuration, options)
            .Enrich.With(mask) // after the settings, so it also masks properties that settings enrichers add
            .CreateLogger();
    }

    /// <summary>
    /// Deletes <c>taskus_*</c> files older than <paramref name="days"/> days from <paramref name="logFolder"/>; every run
    /// adds its own files, so nothing else would ever remove them. 0 keeps them all. A file that cannot be deleted stays.
    /// </summary>
    public static void Cleanup(string logFolder, int days, DateTime now)
    {
        if (days <= 0 || !Directory.Exists(logFolder))
            return;
        foreach (var file in Directory.GetFiles(logFolder, "taskus_*").Where(f => File.GetLastWriteTime(f) < now.AddDays(-days)))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use or protected: the next run tries again
            }
        }
    }

    /// <summary>Serilog level names, plus <c>trace</c> for <c>Verbose</c>.</summary>
    public static bool TryParseLevel(string text, out LogEventLevel level)
    {
        if (string.Equals(text, "trace", StringComparison.OrdinalIgnoreCase))
        {
            level = LogEventLevel.Verbose;
            return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out level) && Enum.IsDefined(level);
    }
}
