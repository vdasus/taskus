using Serilog;
using Taskus.Config;
using Taskus.Pipeline;

namespace Taskus.Transports;

/// <summary>Builds sources and sinks by the <c>type</c> value of the task file.</summary>
public static class Transport
{
    public static Result<ISource> Source(Settings settings, ILogger log) =>
        settings.Required("type").Bind(type => type.ToLowerInvariant() switch
        {
            "excel" => ExcelSource.Create(settings, log),
            "postgres" => PostgresSource.Create(settings),
            "http" => HttpSource.Create(settings, log),
            _ => Result.Failure<ISource>($"{settings.Path}.type: unknown source '{type}' (excel | postgres | http)"),
        });

    public static Result<ISink> Sink(Settings settings, Settings? smtp, ILogger log) =>
        settings.Required("type").Bind(type => type.ToLowerInvariant() switch
        {
            "excel" => ExcelSink.Create(settings),
            "postgres" => PostgresSink.Create(settings),
            "smtp" => SmtpSink.Create(settings, smtp),
            "http" => HttpSink.Create(settings, log),
            _ => Result.Failure<ISink>($"{settings.Path}.type: unknown sink '{type}' (excel | postgres | smtp | http)"),
        });
}
