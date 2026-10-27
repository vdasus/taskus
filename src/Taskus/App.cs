using Serilog;
using System.Text.Json.Nodes;
using Taskus.Config;
using Taskus.Pipeline;

namespace Taskus;

public static class ExitCode
{
    public const int Success = 0;
    public const int Errors = 1;
    public const int Invalid = 2;
}

/// <summary>Runs or validates one task file, or every task file in a folder.</summary>
public sealed class App(ILogger log, SecretMask? secretMask = null)
{
    private readonly SecretMask mask = secretMask ?? new SecretMask();

    public async Task<int> ExecuteAsync(string verb, string path, string secretsFile, bool dryRun, CancellationToken ct)
    {
        var files = TaskFiles(path);
        var secrets = Secrets.Load(secretsFile);
        if (Result.Combine("; ", files, secrets) is { IsFailure: true } failed)
        {
            log.Error("{Error}", failed.Error);
            return ExitCode.Invalid;
        }

        mask.Add(secrets.Value);
        log.Debug("Loaded {SecretCount} secrets from {SecretsFile}", secrets.Value.Count, secretsFile);
        log.Debug("{Verb}: {TaskCount} task files in {Path}", verb, files.Value.Count, path);

        var exit = ExitCode.Success;
        foreach (var file in files.Value)
        {
            var code = verb == "validate"
                ? Validate(file, secrets.Value)
                : await RunAsync(file, secrets.Value, dryRun, ct);
            exit = Math.Max(exit, code);
        }

        return exit;
    }

    private int Validate(string file, JsonObject secrets)
    {
        var task = TaskLoader.Load(file, secrets);
        if (task.IsFailure)
        {
            log.Error("{Error}", task.Error);
            return ExitCode.Invalid;
        }

        log.Information("{File}: valid", file);
        return ExitCode.Success;
    }

    private async Task<int> RunAsync(string file, JsonObject secrets, bool dryRun, CancellationToken ct)
    {
        var loaded = TaskLoader.Load(file, secrets);
        if (loaded.IsFailure)
        {
            log.Error("{Error}", loaded.Error);
            return ExitCode.Invalid;
        }

        var task = loaded.Value;
        var report = await CompositionRoot.Runner(task, log).RunAsync(dryRun, ct);
        report.Redact(mask.Apply); // error texts can quote a URL with a secret in it; the report may go by e-mail
        log.Information("{Task}: finished, {Result}", task.Name, report.HasErrors ? "with errors" : "success");
        if (dryRun)
            return report.HasErrors ? ExitCode.Errors : ExitCode.Success;

        var sent = await Reporter.SendAsync(task, report, log, ct);
        return report.HasErrors || sent.IsFailure ? ExitCode.Errors : ExitCode.Success;
    }

    private static Result<IReadOnlyList<string>> TaskFiles(string path)
    {
        if (File.Exists(path))
            return new[] { path };
        if (!Directory.Exists(path))
            return Result.Failure<IReadOnlyList<string>>($"{path}: no such file or folder");

        return Directory.EnumerateFiles(path, "*.yaml").Concat(Directory.EnumerateFiles(path, "*.yml"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
