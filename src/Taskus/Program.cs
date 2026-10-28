using Optionus;
using System.IO.Abstractions;
using System.Reflection;
using System.Security.Cryptography;
using Serilog;
using Serilog.Events;
using Taskus;
using Taskus.Config;

var dryRun = false;
var help = false;
var currentUser = false;
var secretsFile = Path.Combine(AppContext.BaseDirectory, "secrets.yaml");
var logFolder = Path.Combine(AppContext.BaseDirectory, "logs");
LogEventLevel? logLevel = null;
// The SDK appends "+<commit>" to the informational version; the release number is enough here.
var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];

var options = new OptionSet
{
    $"taskus {version}",
    "Usage: taskus run|validate PATH [OPTIONS]",
    "       taskus schema",
    "       taskus secrets protect|unprotect [--current-user] [--secrets=FILE]",
    "PATH is a task file or a folder of task files (*.yaml, *.yml).",
    "schema prints the JSON Schema of task files.",
    "secrets protect replaces secrets.yaml with secrets.sec, encrypted for this machine (Windows only);",
    "secrets unprotect writes secrets.yaml back for editing. On Windows, run reads only secrets.sec.",
    "",
    "Documentation, task file reference and examples:",
    "  https://github.com/vdasus/taskus",
    "",
    "Options:",
    { "dry-run", "read and map, write nothing", v => dryRun = v != null },
    { "secrets=", "secrets {FILE}, default: secrets.yaml next to taskus; the protected copy has the extension .sec", v => secretsFile = v },
    { "current-user", "secrets protect: only the current Windows account can decrypt, not every account on this machine", v => currentUser = v != null },
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

if (!help && arguments is ["schema"])
{
    Console.Write(TaskSchema.Generate());
    return ExitCode.Success;
}

if (!help && arguments is ["secrets", ("protect" or "unprotect") and var action])
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("taskus: secrets protection needs Windows; here secrets.yaml is read as is, so allow only the account that runs taskus to read it (chmod 600)");
        return ExitCode.Invalid;
    }

    var fs = new FileSystem();
    var done = action == "protect"
        ? SecretsProtection.Protect(fs, secretsFile, SecretsProtection.Protector(currentUser ? DataProtectionScope.CurrentUser : DataProtectionScope.LocalMachine), SecretsProtection.Unprotect)
        : SecretsProtection.Unprotect(fs, secretsFile, SecretsProtection.Unprotect);
    if (done.IsFailure)
    {
        Console.Error.WriteLine($"taskus: {done.Error}");
        return ExitCode.Invalid;
    }

    Console.WriteLine(action == "protect"
        ? $"Protected: {done.Value}. {secretsFile} is deleted; run 'taskus secrets unprotect' to edit it."
        : $"Written: {done.Value}. Edit it, then run 'taskus secrets protect'; until then taskus run stops with an error.");
    return ExitCode.Success;
}

if (help || arguments.Count != 2 || arguments[0] is not ("run" or "validate"))
{
    options.WriteOptionDescriptions(help ? Console.Out : Console.Error);
    return help ? ExitCode.Success : ExitCode.Invalid;
}

var mask = new SecretMask();
Log.Logger = Logging.Create(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), logFolder, DateTime.Now, mask, logLevel);
Log.Information("taskus {Version}", version);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return await new App(Log.Logger, mask, unprotectSecrets: OperatingSystem.IsWindows() ? SecretsProtection.Unprotect : null)
        .ExecuteAsync(arguments[0], arguments[1], secretsFile, dryRun, cts.Token);
}
catch (OperationCanceledException)
{
    Log.Warning("Cancelled");
    return ExitCode.Errors;
}
catch (Exception ex)
{
    // Logged instead of the runtime's unmasked stack trace on the console.
    Log.Fatal("taskus failed: {Error}", ex.Message);
    return ExitCode.Invalid;
}
finally
{
    await Log.CloseAndFlushAsync();
}
