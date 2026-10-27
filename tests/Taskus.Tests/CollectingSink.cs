using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Taskus.Tests;

/// <summary>Keeps log events in memory; <see cref="Text"/> holds every message and property value.</summary>
public sealed class CollectingSink : ILogEventSink
{
    public List<LogEvent> Events { get; } = [];

    public string Text => string.Join("\n", Events.Select(e => e.RenderMessage() + " " + string.Join(" ", e.Properties.Values)));

    public void Emit(LogEvent logEvent) => Events.Add(logEvent);

    public ILogger Logger(SecretMask? mask = null) =>
        new LoggerConfiguration().MinimumLevel.Verbose().Enrich.With(mask ?? new SecretMask()).WriteTo.Sink(this).CreateLogger();
}
