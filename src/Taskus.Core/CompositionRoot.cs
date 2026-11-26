using System.IO.Abstractions;
using Serilog;
using Taskus.Config;
using Taskus.Mapping;
using Taskus.Pipeline;

namespace Taskus;

/// <summary>The one place that turns validated task options into a runner with its source, mapper and sink.</summary>
public static class CompositionRoot
{
    /// <param name="task">Loaded by <see cref="TaskLoader"/>, so already validated.</param>
    public static Runner Runner(TaskOptions task, ILogger log, IFileSystem fs)
    {
        var taskLog = log.ForContext("Task", task.Name);
        taskLog.Debug("Source {Source}, sink {Sink}, {FieldCount} mapped fields, onError {OnError}, maxErrors {MaxErrors}, hidden fields {LogExclude}",
            task.Source.GetType().Name, task.Sink.GetType().Name, task.Map.Count, task.OnError, task.MaxErrors, task.LogExclude);
        var context = new ConnectorContext(task, taskLog, fs);
        return new Runner(task.Name!, Connectors.SourceFor(task.Source).Create(task.Source, context), RowMapper.Create(task.Map).Value,
            Connectors.SinkFor(task.Sink).Create(task.Sink, context), task.OnError, task.MaxErrors, taskLog, task.LogExclude);
    }
}
