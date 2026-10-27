using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Taskus.Config;

// The task file model. These records are bound from YAML, checked by TaskValidation,
// and exported as the JSON Schema of task files (TaskSchema), so descriptions here reach the editor.

public enum ErrorPolicy { Stop, Continue }

public enum ReportWhen { Always, OnError, Never }

public enum TransactionScope { PerRow, All }

public enum HttpMethodName
{
    [JsonStringEnumMemberName("GET")] Get,
    [JsonStringEnumMemberName("POST")] Post,
    [JsonStringEnumMemberName("PUT")] Put,
    [JsonStringEnumMemberName("PATCH")] Patch,
    [JsonStringEnumMemberName("DELETE")] Delete,
}

[Description("A Taskus task: read rows from a source, map each row, write it to a sink.")]
public sealed record TaskOptions
{
    [Description("Task name for logs and report file names. Default: the task file name.")]
    public string? Name { get; init; }

    [Description("Where rows come from.")]
    public required SourceOptions Source { get; init; }

    [Description("Target field name to Scriban template over 'row'. A single {{ expression }} keeps its type. Empty: rows pass through unchanged.")]
    public Dictionary<string, string> Map { get; init; } = [];

    [Description("Where mapped rows go.")]
    public required SinkOptions Sink { get; init; }

    [Description("stop (default): end the run at the first failed row. continue: skip failed rows and report them.")]
    public ErrorPolicy OnError { get; init; } = ErrorPolicy.Stop;

    [Description("Stop a continue run after this many errors over all units. 0: no limit. Default 100.")]
    public int MaxErrors { get; init; } = 100;

    [Description("Run report: saved as .xlsx and/or mailed.")]
    public ReportOptions Report { get; init; } = new();

    [Description("SMTP server for the smtp sink and the report mail. Usually ${secret:name}.")]
    public SmtpOptions? Smtp { get; init; }

    [Description("Row fields whose values are logged as *** (trace and dry-run logs).")]
    public List<string> LogExclude { get; init; } = [];
}

public sealed record ReportOptions
{
    [Description("Folder for the report .xlsx file.")]
    public string? Folder { get; init; }

    [Description("Report e-mail recipients, separated by , or ;. Needs the smtp section.")]
    public string? MailTo { get; init; }

    [Description("always | onError (default) | never.")]
    public ReportWhen When { get; init; } = ReportWhen.OnError;
}

public sealed record SmtpOptions
{
    [Description("SMTP server host name.")]
    public required string Server { get; init; }

    [Description("SMTP port. Default 587.")]
    public int Port { get; init; } = 587;

    [Description("User name; omit for an anonymous server.")]
    public string? User { get; init; }

    [Description("Password. Keep it in secrets.yaml.")]
    public string? Password { get; init; }

    [Description("Sender address.")]
    public required string From { get; init; }

    // The generated record ToString() would print Password.
    public override string ToString() => $"{Server}:{Port} as {User ?? "(anonymous)"}, from {From}";
}

public sealed record RetryOptions
{
    [Description("Extra attempts after the first call. Default 3.")]
    public int Attempts { get; init; } = 3;

    [Description("Seconds before the first retry, doubled for each next one (with jitter). Default 1.")]
    public double Delay { get; init; } = 1;
}

/// <summary>Settings shared by the http source and sink.</summary>
public interface IHttpEndpointOptions
{
    HttpMethodName Method { get; }
    string Url { get; }
    Dictionary<string, string> Headers { get; }
    double Timeout { get; }
    RetryOptions? Retry { get; }
    double RateLimit { get; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ExcelSourceOptions), "excel")]
[JsonDerivedType(typeof(PostgresSourceOptions), "postgres")]
[JsonDerivedType(typeof(HttpSourceOptions), "http")]
public abstract record SourceOptions;

[Description("Every .xlsx file matching pattern in folder is one unit. The first sheet row holds the field names.")]
public sealed record ExcelSourceOptions : SourceOptions
{
    [Description("Input folder.")]
    public required string Folder { get; init; }

    [Description("File name pattern. Default *.xlsx.")]
    public string Pattern { get; init; } = "*.xlsx";

    [Description("Sheet name. Default: the first sheet.")]
    public string? Sheet { get; init; }

    [Description("Folder for files the sink completed.")]
    public string? Processed { get; init; }

    [Description("Folder for files that stopped or were rolled back.")]
    public string? Failed { get; init; }
}

[Description("The result of query is one unit.")]
public sealed record PostgresSourceOptions : SourceOptions
{
    [Description("Npgsql connection string. Usually ${secret:name}.")]
    public required string Connection { get; init; }

    [Description("SQL query; each result row is one row.")]
    public required string Query { get; init; }
}

[Description("Reads rows from one JSON response.")]
public sealed record HttpSourceOptions : SourceOptions, IHttpEndpointOptions
{
    [Description("GET (default) | POST | PUT | PATCH | DELETE.")]
    public HttpMethodName Method { get; init; } = HttpMethodName.Get;

    [Description("Absolute http(s) URL; a Scriban template without row.")]
    public required string Url { get; init; }

    [Description("Request body template, sent as JSON. Needs POST, PUT or PATCH.")]
    public string? Body { get; init; }

    [Description("Dotted path to the array of rows in the response. Default: the response root.")]
    public string? Items { get; init; }

    [Description("Request headers. Content-Type replaces the default.")]
    public Dictionary<string, string> Headers { get; init; } = [];

    [Description("Seconds per attempt. Default 100.")]
    public double Timeout { get; init; } = 100;

    [Description("Retry network errors, time-outs, 408, 429 and 5xx. Omit for no retries.")]
    public RetryOptions? Retry { get; init; }

    [Description("Requests per second. 0 (default): no limit.")]
    public double RateLimit { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ExcelSinkOptions), "excel")]
[JsonDerivedType(typeof(PostgresSinkOptions), "postgres")]
[JsonDerivedType(typeof(SmtpSinkOptions), "smtp")]
[JsonDerivedType(typeof(HttpSinkOptions), "http")]
public abstract record SinkOptions;

[Description("Writes each unit to one .xlsx file.")]
public sealed record ExcelSinkOptions : SinkOptions
{
    [Description("Output folder.")]
    public required string Folder { get; init; }

    [Description("File name template; 'unit' is the input file name without extension.")]
    public required string File { get; init; }

    [Description("Replace an existing file. Default false: the unit fails instead.")]
    public bool Overwrite { get; init; }

    [Description("Sheet name. Default Sheet1.")]
    public string Sheet { get; init; } = "Sheet1";
}

[Description("Inserts rows into a table; columns are the mapped field names.")]
public sealed record PostgresSinkOptions : SinkOptions
{
    [Description("Npgsql connection string. Usually ${secret:name}.")]
    public required string Connection { get; init; }

    [Description("Table name, optionally schema.table.")]
    public required string Table { get; init; }

    [Description("perRow (default): commit each row. all: one transaction per unit, rolled back on the first error.")]
    public TransactionScope Transaction { get; init; } = TransactionScope.PerRow;
}

[Description("Sends one e-mail per row; mapped fields: to, subject, body, cc. Needs the smtp section.")]
public sealed record SmtpSinkOptions : SinkOptions
{
    [Description("Send the body as HTML. Default false.")]
    public bool Html { get; init; }
}

[Description("Calls an HTTP API once per row. POST, PUT and PATCH send the mapped row as JSON.")]
public sealed record HttpSinkOptions : SinkOptions, IHttpEndpointOptions
{
    [Description("GET | POST (default) | PUT | PATCH | DELETE.")]
    public HttpMethodName Method { get; init; } = HttpMethodName.Post;

    [Description("Absolute http(s) URL; a Scriban template over the mapped row.")]
    public required string Url { get; init; }

    [Description("Request headers. Content-Type replaces the default application/json.")]
    public Dictionary<string, string> Headers { get; init; } = [];

    [Description("Seconds per attempt. Default 100.")]
    public double Timeout { get; init; } = 100;

    [Description("Retry network errors, time-outs, 408, 429 and 5xx. Omit for no retries.")]
    public RetryOptions? Retry { get; init; }

    [Description("Requests per second. 0 (default): no limit.")]
    public double RateLimit { get; init; }
}
