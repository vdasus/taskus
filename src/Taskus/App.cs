using Serilog;
using Taskus.Config;
using Taskus.Mapping;
using Taskus.Pipeline;
using Taskus.Transports;

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

    private int Validate(string file, IReadOnlyDictionary<string, object?> secrets)
    {
        var job = Build(file, secrets);
        if (job.IsFailure)
        {
            log.Error("{Error}", job.Error);
            return ExitCode.Invalid;
        }

        log.Information("{File}: valid", file);
        return ExitCode.Success;
    }

    private async Task<int> RunAsync(string file, IReadOnlyDictionary<string, object?> secrets, bool dryRun, CancellationToken ct)
    {
        var job = Build(file, secrets);
        if (job.IsFailure)
        {
            log.Error("{Error}", job.Error);
            return ExitCode.Invalid;
        }

        var (task, runner) = job.Value;
        var report = await runner.RunAsync(dryRun, ct);
        report.Redact(mask.Apply); // error texts can quote a URL with a secret in it; the report may go by e-mail
        log.Information("{Task}: finished, {Result}", task.Name, report.HasErrors ? "with errors" : "success");
        if (dryRun)
            return report.HasErrors ? ExitCode.Errors : ExitCode.Success;

        var sent = await Reporter.SendAsync(task, report, log, ct);
        return report.HasErrors || sent.IsFailure ? ExitCode.Errors : ExitCode.Success;
    }

    private Result<(TaskDefinition Task, Runner Runner)> Build(string file, IReadOnlyDictionary<string, object?> secrets) =>
        TaskLoader.Load(file, secrets).Bind(task =>
        {
            var taskLog = log.ForContext("Task", task.Name);
            taskLog.Debug("{File}: source {SourceType}, sink {SinkType}, {FieldCount} mapped fields, onError {OnError}, maxErrors {MaxErrors}, hidden fields {LogExclude}",
                file, task.Source.Optional("type"), task.Sink.Optional("type"), task.Map.Count, task.OnError, task.MaxErrors, task.LogExclude);
            var mapper = RowMapper.Create(task.Map);
            var source = Transport.Source(task.Source, taskLog);
            var sink = Transport.Sink(task.Sink, task.Smtp, taskLog);
            return Result.Combine("; ", mapper, source, sink)
                .MapError(error => $"{file}: {error}")
                .Map(() => (task, new Runner(task.Name, source.Value, mapper.Value, sink.Value, task.OnError, task.MaxErrors, taskLog, task.LogExclude)));
        });

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
