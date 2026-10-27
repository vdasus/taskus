using Optionus;
using Serilog;
using Taskus;

var dryRun = false;
var help = false;
var secretsFile = Path.Combine(AppContext.BaseDirectory, "secrets.yaml");
var logFolder = Path.Combine(AppContext.BaseDirectory, "logs");

var options = new OptionSet
{
    "Usage: taskus run|validate PATH [OPTIONS]",
    "PATH is a task file or a folder of task files (*.yaml, *.yml).",
    "",
    "Options:",
    { "dry-run", "read and map, write nothing", v => dryRun = v != null },
    { "secrets=", "secrets {FILE}, default: secrets.yaml next to taskus", v => secretsFile = v },
    { "logs=", "log {FOLDER} for relative log paths in appsettings.json, default: logs next to taskus", v => logFolder = v },
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

Log.Logger = Logging.Create(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), logFolder, DateTime.Now);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return await new App(Log.Logger).ExecuteAsync(arguments[0], arguments[1], secretsFile, dryRun, cts.Token);
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
