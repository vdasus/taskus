using Optionus;
using Serilog;
using Serilog.Events;
using Taskus;

var dryRun = false;
var help = false;
var secretsFile = Path.Combine(AppContext.BaseDirectory, "secrets.yaml");
var logFolder = Path.Combine(AppContext.BaseDirectory, "logs");
LogEventLevel? logLevel = null;

var options = new OptionSet
{
    "Usage: taskus run|validate PATH [OPTIONS]",
    "PATH is a task file or a folder of task files (*.yaml, *.yml).",
    "",
    "Documentation, task file reference and examples:",
    "  https://github.com/vdasus/taskus",
    "",
    "Options:",
    { "dry-run", "read and map, write nothing", v => dryRun = v != null },
    { "secrets=", "secrets {FILE}, default: secrets.yaml next to taskus", v => secretsFile = v },
    { "logs=", "log {FOLDER}, default: logs next to taskus", v => logFolder = v },
    {
        "log-level=", "minimum log {LEVEL}: trace | debug | information | warning | error, default: from appsettings.json",
        v => logLevel = Logging.TryParseLevel(v, out var level) ? level : throw new OptionException($"unknown log level '{v}'", "--log-level")
    },
    { "h|help", "show this help", v => help = v != null },
};

List<string> arguments;
try
{
    arguments = options.Parse(args);
}
catch (OptionException e)
{
    Console.Error.WriteLine($"taskus: {e.Message}");
    return ExitCode.Invalid;
}

if (help || arguments.Count != 2 || arguments[0] is not ("run" or "validate"))
{
    options.WriteOptionDescriptions(help ? Console.Out : Console.Error);
    return help ? ExitCode.Success : ExitCode.Invalid;
}

var mask = new SecretMask();
Log.Logger = Logging.Create(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), logFolder, DateTime.Now, mask, logLevel);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return await new App(Log.Logger, mask).ExecuteAsync(arguments[0], arguments[1], secretsFile, dryRun, cts.Token);
}
catch (OperationCanceledException)
{
    Log.Warning("Cancelled");
    return ExitCode.Errors;
}
finally
{
    await Log.CloseAndFlushAsync();
}
