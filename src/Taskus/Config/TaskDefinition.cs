using YamlDotNet.Core;

namespace Taskus.Config;

public enum ErrorPolicy { Stop, Continue }

public enum ReportWhen { Always, OnError, Never }

public sealed record ReportOptions(string? Folder, string? MailTo, ReportWhen When)
{
    public static readonly ReportOptions Default = new(null, null, ReportWhen.OnError);
}

public sealed record TaskDefinition(
    string Name,
    Settings Source,
    IReadOnlyList<KeyValuePair<string, string>> Map,
    Settings Sink,
    ErrorPolicy OnError,
    int MaxErrors,
    ReportOptions Report,
    Settings? Smtp,
    IReadOnlyList<string> LogExclude);

public static class TaskLoader
{
    public static Result<TaskDefinition> Load(string file, IReadOnlyDictionary<string, object?> secrets)
    {
        object? tree;
        try
        {
            tree = Yaml.Deserialize(File.ReadAllText(file));
        }
        catch (YamlException ex)
        {
            return Result.Failure<TaskDefinition>($"{file}: {ex.Message}");
        }

        return Secrets.Resolve(tree, secrets)
            .Bind(t => Settings.From("", t))
            .Bind(root => Build(Path.GetFileNameWithoutExtension(file), root))
            .MapError(error => $"{file}: {error}");
    }

    private static Result<TaskDefinition> Build(string fallbackName, Settings root)
    {
        var source = root.Section("source");
        var sink = root.Section("sink");
        var map = root.StringMap("map");
        var onError = root.Enum("onError", ErrorPolicy.Stop);
        var maxErrors = root.Int("maxErrors", 100);
        var smtp = root.OptionalSection("smtp");
        var report = root.OptionalSection("report").Bind(ReadReport);
        var logExclude = root.StringList("logExclude");

        return Result.Combine("; ", source, sink, map, onError, maxErrors, smtp, report, logExclude)
            .Ensure(() => report.Value.MailTo is null || smtp.Value is not null, "report.mailTo needs an smtp section")
            .Map(() => new TaskDefinition(
                root.Optional("name") ?? fallbackName,
                source.Value, map.Value, sink.Value, onError.Value, maxErrors.Value, report.Value, smtp.Value, logExclude.Value));
    }

    private static Result<ReportOptions> ReadReport(Settings? report) =>
        report is null
            ? ReportOptions.Default
            : report.Enum("when", ReportWhen.OnError)
                .Map(when => new ReportOptions(report.Optional("folder"), report.Optional("mailTo"), when));
}
