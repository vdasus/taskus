using Serilog;
using Taskus.Config;
using Taskus.Mapping;
using Taskus.Pipeline;
using Taskus.Transports;

namespace Taskus;

/// <summary>The one place that turns validated task options into a runner with its source, mapper and sink.</summary>
public static class CompositionRoot
{
    /// <param name="task">Loaded by <see cref="TaskLoader"/>, so already validated.</param>
    public static Runner Runner(TaskOptions task, ILogger log)
    {
        var taskLog = log.ForContext("Task", task.Name);
        taskLog.Debug("Source {Source}, sink {Sink}, {FieldCount} mapped fields, onError {OnError}, maxErrors {MaxErrors}, hidden fields {LogExclude}",
            task.Source.GetType().Name, task.Sink.GetType().Name, task.Map.Count, task.OnError, task.MaxErrors, task.LogExclude);
        return new Runner(task.Name!, Source(task.Source, taskLog), RowMapper.Create(task.Map).Value, Sink(task.Sink, task.Smtp, taskLog),
            task.OnError, task.MaxErrors, taskLog, task.LogExclude);
    }

    private static ISource Source(SourceOptions options, ILogger log) => options switch
    {
        ExcelSourceOptions o => new ExcelSource(o.Folder, o.Pattern, o.Sheet, o.Processed, o.Failed, log),
        PostgresSourceOptions o => new PostgresSource(o.Connection, o.Query, log),
        HttpSourceOptions o => new HttpSource(
            new HttpEndpoint(o, "source", log, new HttpClientHandler()), o.Body is null ? null : TaskTemplate.Parse(o.Body).Value, o.Items),
        _ => throw new NotSupportedException(options.GetType().Name),
    };

    private static ISink Sink(SinkOptions options, SmtpOptions? smtp, ILogger log) => options switch
    {
        ExcelSinkOptions o => new ExcelSink(o.Folder, TaskTemplate.Parse(o.File).Value, o.Overwrite, o.Sheet, log),
        PostgresSinkOptions o => new PostgresSink(o.Connection, o.Table, o.Transaction, log),
        SmtpSinkOptions o => new SmtpSink(smtp!, o.Html, log),
        HttpSinkOptions o => new HttpSink(new HttpEndpoint(o, "sink", log, new HttpClientHandler())),
        _ => throw new NotSupportedException(options.GetType().Name),
    };
}
