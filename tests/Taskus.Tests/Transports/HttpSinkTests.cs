using System.Net;
using FluentAssertions;
using Serilog.Core;
using Taskus.Config;
using Taskus.Pipeline;
using Taskus.Transports;

namespace Taskus.Tests.Transports;

public class HttpSinkTests
{
    private static Result<ISink> Create(FakeHandler handler, Dictionary<string, object?> settings) =>
        HttpSink.Create(Settings.Of("sink", settings), Logger.None, handler);

    private static async Task<Result> WriteAsync(ISink sink, Row row)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = (await sink.OpenAsync("u", ct)).Value;
        return await session.WriteAsync(row, ct);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public async Task Post_SendsMappedRowAsJson()
    {
        var handler = new FakeHandler();
        var sink = Create(handler, new() { ["url"] = "http://example.com/api" }).Value;

        var result = await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 42.0 });

        result.IsSuccess.Should().BeTrue();
        var call = handler.Calls.Single();
        call.Method.Should().Be(HttpMethod.Post);
        call.Url.Should().Be("http://example.com/api");
        call.Body.Should().Be("""{"id":42}""");
        call.Request.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public async Task Get_RendersUrlAndSendsNoBody()
    {
        var handler = new FakeHandler();
        var sink = Create(handler, new() { ["method"] = "get", ["url"] = "http://example.com/api/items/{{ row.id }}" }).Value;

        await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 7 });

        var call = handler.Calls.Single();
        call.Method.Should().Be(HttpMethod.Get);
        call.Url.Should().Be("http://example.com/api/items/7");
        call.Body.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Headers_GoToRequestAndContent()
    {
        var handler = new FakeHandler();
        var headers = new Dictionary<object, object?> { ["Authorization"] = "Bearer t0k", ["Content-Type"] = "application/vnd.api+json" };
        var sink = Create(handler, new() { ["url"] = "http://example.com/api", ["headers"] = headers }).Value;

        await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 1 });

        var request = handler.Calls.Single().Request;
        request.Headers.Authorization!.ToString().Should().Be("Bearer t0k");
        request.Content!.Headers.ContentType!.MediaType.Should().Be("application/vnd.api+json");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ErrorStatus_FailsRowWithStatusAndBody()
    {
        var sink = Create(new FakeHandler(HttpStatusCode.NotFound, "no such id"), new() { ["url"] = "http://example.com/api" }).Value;

        var result = await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 1 });

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("POST http://example.com/api: HTTP 404 Not Found no such id");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RelativeUrl_FailsRowWithoutCall()
    {
        var handler = new FakeHandler();
        var sink = Create(handler, new() { ["url"] = "{{ row.path }}" }).Value;

        var result = await WriteAsync(sink, new Dictionary<string, object?> { ["path"] = "/api" });

        result.Error.Should().Contain("not an absolute http(s) URL");
        handler.Calls.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_InvalidSettings_ReportsEach()
    {
        var result = Create(new FakeHandler(), new() { ["method"] = "TRACE", ["timeout"] = "0" });

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("sink.method 'TRACE' must be one of")
            .And.Contain("sink.url is required")
            .And.Contain("sink.timeout must be positive");
    }

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("") };

    private static Dictionary<object, object?> Retry(int attempts) => new() { ["attempts"] = attempts.ToString(), ["delay"] = "0" };

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public async Task Retry_ServerErrorThenSuccess_Succeeds()
    {
        var handler = new FakeHandler(call => Status(call == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        var sink = Create(handler, new() { ["url"] = "http://example.com/api", ["retry"] = Retry(3) }).Value;

        var result = await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 1 });

        result.IsSuccess.Should().BeTrue();
        handler.Calls.Should().HaveCount(2);
        handler.Calls.Select(c => c.Body).Should().AllBe("{\"id\":1}", "the body is rebuilt for every attempt");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Retry_Exhausted_FailsWithLastStatus()
    {
        var handler = new FakeHandler(HttpStatusCode.TooManyRequests);
        var sink = Create(handler, new() { ["url"] = "http://example.com/api", ["retry"] = Retry(2) }).Value;

        var result = await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 1 });

        handler.Calls.Should().HaveCount(3);
        result.Error.Should().Be("POST http://example.com/api: HTTP 429 Too Many Requests");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Retry_ClientError_IsNotRetried()
    {
        var handler = new FakeHandler(HttpStatusCode.BadRequest);
        var sink = Create(handler, new() { ["url"] = "http://example.com/api", ["retry"] = Retry(3) }).Value;

        await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 1 });

        handler.Calls.Should().ContainSingle();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Retry_NetworkError_IsRetried()
    {
        var handler = new FakeHandler(call => call == 1 ? throw new HttpRequestException("connection refused") : Status(HttpStatusCode.OK));
        var sink = Create(handler, new() { ["url"] = "http://example.com/api", ["retry"] = Retry(1) }).Value;

        var result = await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 1 });

        result.IsSuccess.Should().BeTrue();
        handler.Calls.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NoRetrySection_CallsOnce()
    {
        var handler = new FakeHandler(HttpStatusCode.ServiceUnavailable);
        var sink = Create(handler, new() { ["url"] = "http://example.com/api" }).Value;

        await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 1 });

        handler.Calls.Should().ContainSingle();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RateLimit_SpacesRequests()
    {
        var sink = Create(new FakeHandler(), new() { ["url"] = "http://example.com/api", ["rateLimit"] = "20" }).Value;
        var ct = TestContext.Current.CancellationToken;
        await using var session = (await sink.OpenAsync("u", ct)).Value;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 3; i++)
            await session.WriteAsync(new Dictionary<string, object?> { ["id"] = i }, ct);

        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(90), "3 calls at 20/s need two 50 ms gaps");
    }
}
