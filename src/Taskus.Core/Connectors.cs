using System.IO.Abstractions;
using System.Text.Json.Serialization.Metadata;
using FluentValidation;
using Serilog;
using Taskus.Config;
using Taskus.Mapping;
using Taskus.Pipeline;
using Taskus.Transports;
using static Taskus.Config.TaskValidation;

namespace Taskus;

/// <summary>What a connector factory gets besides its own options.</summary>
public sealed record ConnectorContext(TaskOptions Task, ILogger Log, IFileSystem Fs);

/// <summary>The source or sink half of a connector: its options type, the rules for those options and the factory.</summary>
public sealed record ConnectorPart<TOptions, TResult>(Type OptionsType, IValidator Validator, Func<TOptions, ConnectorContext, TResult> Create);

/// <summary>One <c>type</c> of a task file section. A connector provides a source, a sink or both.</summary>
public sealed record Connector(string Type, ConnectorPart<SourceOptions, ISource>? Source, ConnectorPart<SinkOptions, ISink>? Sink);

/// <summary>
/// Every connector Taskus knows. Task binding, validation, the JSON Schema, <see cref="CompositionRoot"/> and Taskus Studio
/// read this list, so a new connector is one entry here.
/// </summary>
public static class Connectors
{
    /// <summary>In the order the schema and Studio list them.</summary>
    public static IReadOnlyList<Connector> All { get; } =
    [
        new("excel",
            Source<ExcelSourceOptions>(new ExcelSourceValidator(),
                (o, c) => new ExcelSource(c.Fs, o.Folder, o.Pattern, o.Sheet, o.Processed, o.Failed, c.Log)),
            Sink<ExcelSinkOptions>(new ExcelSinkValidator(),
                (o, c) => new ExcelSink(c.Fs, o.Folder, TaskTemplate.Parse(o.File).Value, o.Overwrite, o.Sheet, c.Log))),
        new("postgres",
            Source<PostgresSourceOptions>(new DbSourceValidator<PostgresSourceOptions>(),
                (o, c) => new DbSource(PostgresDialect.Instance, o.Connection, o.Query, c.Log)),
            Sink<PostgresSinkOptions>(new DbSinkValidator<PostgresSinkOptions>(),
                (o, c) => new DbSink(PostgresDialect.Instance, o.Connection, o.Table, o.Transaction, c.Log))),
        new("odbc",
            Source<OdbcSourceOptions>(new DbSourceValidator<OdbcSourceOptions>(),
                (o, c) => new DbSource(OdbcDialect.Instance, o.Connection, o.Query, c.Log)),
            Sink<OdbcSinkOptions>(new DbSinkValidator<OdbcSinkOptions>(),
                (o, c) => new DbSink(OdbcDialect.Instance, o.Connection, o.Table, o.Transaction, c.Log))),
        new("smtp",
            null,
            // The smtp section it needs is a task-level rule in TaskValidation.
            Sink<SmtpSinkOptions>(new InlineValidator<SmtpSinkOptions>(), (o, c) => new SmtpSink(c.Task.Smtp!, o.Html, c.Log))),
        new("http",
            Source<HttpSourceOptions>(new HttpSourceValidator(),
                (o, c) => new HttpSource(new HttpEndpoint(o, "source", c.Log, Handler()), o.Body is null ? null : TaskTemplate.Parse(o.Body).Value, o.Items)),
            Sink<HttpSinkOptions>(new HttpSinkValidator(),
                (o, c) => new HttpSink(new HttpEndpoint(o, "sink", c.Log, Handler()), o.Body is null ? null : TaskTemplate.Parse(o.Body).Value))),
    ];

    public static IReadOnlyList<string> SourceTypes { get; } = [.. All.Where(c => c.Source is not null).Select(c => c.Type)];

    public static IReadOnlyList<string> SinkTypes { get; } = [.. All.Where(c => c.Sink is not null).Select(c => c.Type)];

    /// <summary>The options record of the source <paramref name="type"/>, e.g. <c>excel</c> → <see cref="ExcelSourceOptions"/>.</summary>
    public static Type SourceOptionsType(string type) =>
        All.SingleOrDefault(c => c.Type == type)?.Source?.OptionsType ?? throw new KeyNotFoundException($"no source type '{type}'");

    /// <summary>The options record of the sink <paramref name="type"/>, e.g. <c>smtp</c> → <see cref="SmtpSinkOptions"/>.</summary>
    public static Type SinkOptionsType(string type) =>
        All.SingleOrDefault(c => c.Type == type)?.Sink?.OptionsType ?? throw new KeyNotFoundException($"no sink type '{type}'");

    internal static ConnectorPart<SourceOptions, ISource> SourceFor(SourceOptions options) =>
        All.Select(c => c.Source).Single(p => p?.OptionsType == options.GetType())!;

    internal static ConnectorPart<SinkOptions, ISink> SinkFor(SinkOptions options) =>
        All.Select(c => c.Sink).Single(p => p?.OptionsType == options.GetType())!;

    /// <summary>System.Text.Json modifier: binds <c>type</c> to the options record of each connector.</summary>
    internal static void AddDerivedTypes(JsonTypeInfo info)
    {
        IEnumerable<(string Type, Type Options)>? derived =
            info.Type == typeof(SourceOptions) ? All.Where(c => c.Source is not null).Select(c => (c.Type, c.Source!.OptionsType))
            : info.Type == typeof(SinkOptions) ? All.Where(c => c.Sink is not null).Select(c => (c.Type, c.Sink!.OptionsType))
            : null;
        if (derived is null)
            return;
        info.PolymorphismOptions = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "type" };
        foreach (var (type, options) in derived)
            info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(options, type));
    }

    private static ConnectorPart<SourceOptions, ISource> Source<T>(IValidator<T> validator, Func<T, ConnectorContext, ISource> create) where T : SourceOptions =>
        new(typeof(T), validator, (o, c) => create((T)o, c));

    private static ConnectorPart<SinkOptions, ISink> Sink<T>(IValidator<T> validator, Func<T, ConnectorContext, ISink> create) where T : SinkOptions =>
        new(typeof(T), validator, (o, c) => create((T)o, c));

    // A redirect would carry custom headers (X-Api-Key) to another host and turn a POST into a GET: report 3xx instead.
    private static HttpClientHandler Handler() => new() { AllowAutoRedirect = false };
}
