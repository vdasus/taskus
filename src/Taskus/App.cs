using Serilog;
using System.IO.Abstractions;
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
/// <param name="unprotectSecrets">DPAPI on Windows: secrets must then be protected; null reads plain <c>secrets.yaml</c>.</param>
public sealed class App(ILogger log, SecretMask? secretMask = null, IFileSystem? fileSystem = null, Func<byte[], byte[]>? unprotectSecrets = null)
{
    private readonly SecretMask mask = secretMask ?? new SecretMask();
    private readonly IFileSystem fs = fileSystem ?? new FileSystem();

    public async Task<int> ExecuteAsync(string verb, string path, string secretsFile, bool dryRun, CancellationToken ct)
    {
        var files = TaskFiles(path);
        var secrets = Secrets.Load(fs, secretsFile, unprotectSecrets);
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
            int code;
            try
            {
                code = verb == "validate"
                    ? Validate(file, secrets.Value)
                    : await RunAsync(file, secrets.Value, dryRun, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken task must not stop the others. The message goes as a property, so secrets are masked.
                log.Error("{File}: {Error}", file, ex.Message);
                code = ExitCode.Invalid;
            }

            exit = Math.Max(exit, code);
        }

        return exit;
    }

    private int Validate(string file, JsonObject secrets)
    {
        var task = TaskLoader.Load(fs, file, secrets);
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
        var loaded = TaskLoader.Load(fs, file, secrets);
        if (loaded.IsFailure)
        {
            log.Error("{Error}", loaded.Error);
            return ExitCode.Invalid;
        }

        var task = loaded.Value;
        var report = await CompositionRoot.Runner(task, log, fs).RunAsync(dryRun, ct);
        report.Redact(mask.Apply); // error texts can quote a URL with a secret in it; the report may go by e-mail
        log.Information("{Task}: finished, {Result}", task.Name, report.HasErrors ? "with errors" : "success");
        if (dryRun)
            return report.HasErrors ? ExitCode.Errors : ExitCode.Success;

        var sent = await Reporter.SendAsync(task, report, log, fs, ct);
        return report.HasErrors || sent.IsFailure ? ExitCode.Errors : ExitCode.Success;
    }

    private Result<IReadOnlyList<string>> TaskFiles(string path)
    {
        if (fs.File.Exists(path))
            return new[] { path };
        if (!fs.Directory.Exists(path))
            return Result.Failure<IReadOnlyList<string>>($"{path}: no such file or folder");

        return fs.Directory.EnumerateFiles(path, "*.yaml").Concat(fs.Directory.EnumerateFiles(path, "*.yml"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
