using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Polly;
using Polly.Retry;
using Serilog;
using Taskus.Config;
using Taskus.Mapping;
using Taskus.Pipeline;

namespace Taskus.Transports;

/// <summary>
/// HTTP settings shared by the http source and sink: method, url template, headers, timeout, retry, rate limit.
/// </summary>
public sealed class HttpEndpoint
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);
    private const int ErrorBodyLength = 300;

    private readonly HttpClient client;
    private readonly TaskTemplate url;
    private readonly IReadOnlyList<KeyValuePair<string, string>> headers;
    private readonly ResiliencePipeline<HttpResponseMessage> pipeline;
    private readonly Throttle? throttle;
    private readonly string path;
    private readonly ILogger log;

    /// <param name="options">Already checked by <see cref="TaskValidation"/>.</param>
    /// <param name="path">Task file section for messages: <c>source</c> or <c>sink</c>.</param>
    public HttpEndpoint(IHttpEndpointOptions options, string path, ILogger log, HttpMessageHandler handler)
    {
        // Header names only: values carry tokens.
        log.Debug("{Path}: {Method} {Url}, timeout {Timeout} s, retry {Retry}, rate limit {RateLimit}/s, headers {Headers}",
            path, options.Method, options.Url, options.Timeout, options.Retry, options.RateLimit, options.Headers.Keys);
        Method = new HttpMethod(options.Method.ToString().ToUpperInvariant());
        // shortcut: never disposed, one per task and the process exits after the run; dispose it if Taskus becomes a service.
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(options.Timeout) };
        url = TaskTemplate.Parse(options.Url).Value;
        headers = [.. options.Headers];
        pipeline = options.Retry is { Attempts: > 0 } retry
            ? new ResiliencePipelineBuilder<HttpResponseMessage>().AddRetry(RetryStrategy(retry, Method, log)).Build()
            : ResiliencePipeline<HttpResponseMessage>.Empty;
        throttle = options.RateLimit > 0 ? new Throttle(options.RateLimit, TimeProvider.System) : null;
        this.path = path;
        this.log = log;
    }

    public HttpMethod Method { get; }

    public bool SendsBody => Method == HttpMethod.Post || Method == HttpMethod.Put || Method == HttpMethod.Patch;

    /// <summary>Renders <c>url</c> with the given variables and checks it is an absolute http(s) URL.</summary>
    public Result<string> Url(IReadOnlyDictionary<string, object?> variables) =>
        url.Evaluate($"{path}.url", variables)
            .Map(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
            .Ensure(
                value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
                value => $"{path}.url '{value}' is not an absolute http(s) URL");

    /// <summary>
    /// Sends the request, with retries and rate limit. Success holds a 2xx response the caller disposes;
    /// any other status, network error or time-out after the last attempt is a failure.
    /// </summary>
    /// <param name="content">Called once per attempt: a sent request cannot be sent again.</param>
    public async Task<Result<HttpResponseMessage>> SendAsync(string target, Func<HttpContent?> content, CancellationToken ct)
    {
        try
        {
            var response = await pipeline.ExecuteAsync(async token =>
            {
                if (throttle is not null)
                    await throttle.WaitAsync(token);
                using var request = Request(target, content());
                var clock = Stopwatch.StartNew();
                var answer = await client.SendAsync(request, token);
                log.Verbose("{Method} {Url}: HTTP {Status} in {ElapsedMs} ms", Method, target, (int)answer.StatusCode, clock.ElapsedMilliseconds);
                return answer;
            }, ct);

            if (response.IsSuccessStatusCode)
                return response;

            using (response)
            {
                // An HTML error page says nothing useful in a log line; API error bodies (JSON, text) do.
                var body = response.Content.Headers.ContentType?.MediaType == "text/html" ? "" : await response.Content.ReadAsStringAsync(ct);
                var detail = body.Length > ErrorBodyLength ? body[..ErrorBodyLength] + "..." : body;
                return Result.Failure<HttpResponseMessage>($"{Method} {target}: HTTP {(int)response.StatusCode} {response.ReasonPhrase} {detail}".TrimEnd());
            }
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<HttpResponseMessage>($"{Method} {target}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result.Failure<HttpResponseMessage>($"{Method} {target}: timed out after {client.Timeout.TotalSeconds:0.#} s");
        }
    }

    private HttpRequestMessage Request(string target, HttpContent? content)
    {
        var request = new HttpRequestMessage(Method, target) { Content = content };
        foreach (var (name, value) in headers)
        {
            // Content headers (Content-Type) cannot go on the request; they replace the content's own.
            if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is { } body)
            {
                body.Headers.Remove(name);
                body.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    // Retries network errors, time-outs, 408, 429 and 5xx. Other 4xx mean the request itself is wrong.
    private static RetryStrategyOptions<HttpResponseMessage> RetryStrategy(RetryOptions retry, HttpMethod method, ILogger log) => new()
    {
        MaxRetryAttempts = retry.Attempts,
        Delay = TimeSpan.FromSeconds(retry.Delay),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
            .HandleResult(r => r.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)r.StatusCode >= 500),
        DelayGenerator = args => ValueTask.FromResult(RetryAfter(args.Outcome.Result)),
        OnRetry = args =>
        {
            var reason = args.Outcome.Exception?.Message ?? $"HTTP {(int)args.Outcome.Result!.StatusCode}";
            log.Warning("{Method} {Url}: attempt {Attempt} failed ({Reason}), retrying in {Delay:0.#} s",
                method, args.Outcome.Result?.RequestMessage?.RequestUri, args.AttemptNumber + 1, reason, args.RetryDelay.TotalSeconds);
            return ValueTask.CompletedTask;
        },
    };

    // The server's Retry-After wins over the backoff, capped so one answer cannot stall a scheduled run.
    private static TimeSpan? RetryAfter(HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers.RetryAfter;
        var wait = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return wait is { } w && w > TimeSpan.Zero ? (w < MaxRetryAfter ? w : MaxRetryAfter) : null;
    }
}

/// <summary>Spaces requests at least 1/<c>perSecond</c> apart. Rows are processed one by one, so no queue is needed.</summary>
internal sealed class Throttle(double perSecond, TimeProvider time)
{
    private readonly TimeSpan interval = TimeSpan.FromSeconds(1 / perSecond);
    private DateTimeOffset next = DateTimeOffset.MinValue;

    public async Task WaitAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (next > now)
            await Task.Delay(next - now, time, ct);
        next = (next > now ? next : now) + interval;
    }
}

/// <summary>
/// Calls an HTTP API once per row. <c>url</c> is a template over the mapped <c>row</c>.
/// POST, PUT and PATCH send the mapped row as a JSON object; GET and DELETE send no body.
/// Any 2xx status is success; the response body is not kept.
/// </summary>
public sealed class HttpSink(HttpEndpoint endpoint) : ISink
{
    public Task<Result<ISinkSession>> OpenAsync(string unit, CancellationToken ct) =>
        Task.FromResult(Result.Success<ISinkSession>(new Session(endpoint)));

    private sealed class Session(HttpEndpoint endpoint) : ISinkSession
    {
        public bool AllOrNothing => false; // a call that reached the API cannot be undone

        public async Task<Result> WriteAsync(Row row, CancellationToken ct)
        {
            var target = endpoint.Url(new Dictionary<string, object?> { ["row"] = row });
            if (target.IsFailure)
                return target;

            var sent = await endpoint.SendAsync(target.Value, () => endpoint.SendsBody ? JsonContent.Create(row) : null, ct);
            if (sent.IsSuccess)
                sent.Value.Dispose();
            return sent;
        }

        public Task<Result> CompleteAsync(CancellationToken ct) => Task.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Reads rows from one JSON response. <c>items</c> is the dotted path to the array of rows (default: the root).
/// An array element that is an object is one row; any other element becomes a row with one field, <c>value</c>.
/// A single object is one row. Nested objects and arrays stay nested: <c>row.address.city</c>.
/// </summary>
public sealed class HttpSource(HttpEndpoint endpoint, TaskTemplate? body, string? items) : ISource
{
    public IEnumerable<SourceUnit> Units()
    {
        yield return new SourceUnit("response", ReadAsync(), _ => { });
    }

    // A failed call throws: the runner records it as the unit's failure.
    private async IAsyncEnumerable<SourceRow> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var none = new Dictionary<string, object?>();
        var target = OrThrow(endpoint.Url(none));
        var content = body is null ? null : Convert.ToString(OrThrow(body.Evaluate("source.body", none)), CultureInfo.InvariantCulture);
        using var response = OrThrow(await endpoint.SendAsync(
            target, () => content is null ? null : new StringContent(content, Encoding.UTF8, "application/json"), ct));

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = Find(json.RootElement);
        var rows = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];

        var number = 0;
        foreach (var element in rows)
            yield return new SourceRow(++number, element.ValueKind == JsonValueKind.Object
                ? Json.Object(element)
                : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["value"] = Json.Value(element) });
    }

    private static T OrThrow<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error);

    private JsonElement Find(JsonElement root)
    {
        if (string.IsNullOrEmpty(items))
            return root;

        var current = root;
        foreach (var name in items.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                throw new InvalidOperationException($"source.items '{items}' is not in the response");
        }

        return current;
    }
}

internal static class Json
{
    public static Dictionary<string, object?> Object(JsonElement element) =>
        element.EnumerateObject().ToDictionary(p => p.Name, p => Value(p.Value), StringComparer.OrdinalIgnoreCase);

    public static object? Value(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.TryGetDecimal(out var d) ? d : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object => Object(element),
        JsonValueKind.Array => element.EnumerateArray().Select(Value).ToList(),
        _ => null,
    };
}
