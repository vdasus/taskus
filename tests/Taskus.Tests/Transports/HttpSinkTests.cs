using System.Net;
using FluentAssertions;
using Serilog.Core;
using Taskus.Config;
using Taskus.Pipeline;
using Taskus.Transports;

namespace Taskus.Tests.Transports;

public class HttpSinkTests
{
    private const string Api = "http://example.com/api";

    private static ISink Create(FakeHandler handler, HttpSinkOptions options) =>
        new HttpSink(new HttpEndpoint(options, "sink", Logger.None, handler));

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
        var sink = Create(handler, new() { Url = Api });

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
        var sink = Create(handler, new() { Method = HttpMethodName.Get, Url = Api + "/items/{{ row.id }}" });

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
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer t0k", ["Content-Type"] = "application/vnd.api+json" };
        var sink = Create(handler, new() { Url = Api, Headers = headers });

        await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 1 });

        var request = handler.Calls.Single().Request;
        request.Headers.Authorization!.ToString().Should().Be("Bearer t0k");
        request.Content!.Headers.ContentType!.MediaType.Should().Be("application/vnd.api+json");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ErrorStatus_FailsRowWithStatusAndBody()
    {
        var sink = Create(new FakeHandler(HttpStatusCode.NotFound, "no such id"), new() { Url = Api });

        var result = await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 1 });

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("POST http://example.com/api: HTTP 404 Not Found no such id");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RelativeUrl_FailsRowWithoutCall()
    {
        var handler = new FakeHandler();
        var sink = Create(handler, new() { Url = "{{ row.path }}" });

        var result = await WriteAsync(sink, new Dictionary<string, object?> { ["path"] = "/api" });

        result.Error.Should().Contain("not an absolute http(s) URL");
        handler.Calls.Should().BeEmpty();
    }

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("") };

    private static RetryOptions Retry(int attempts) => new() { Attempts = attempts, Delay = 0 };

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public async Task Retry_ServerErrorThenSuccess_Succeeds()
    {
        var handler = new FakeHandler(call => Status(call == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        var sink = Create(handler, new() { Url = Api, Retry = Retry(3) });

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
        var sink = Create(handler, new() { Url = Api, Retry = Retry(2) });

        var result = await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 1 });

        handler.Calls.Should().HaveCount(3);
        result.Error.Should().Be("POST http://example.com/api: HTTP 429 Too Many Requests");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Retry_ClientError_IsNotRetried()
    {
        var handler = new FakeHandler(HttpStatusCode.BadRequest);
        var sink = Create(handler, new() { Url = Api, Retry = Retry(3) });

        await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 1 });

        handler.Calls.Should().ContainSingle();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Retry_NetworkError_IsRetried()
    {
        var handler = new FakeHandler(call => call == 1 ? throw new HttpRequestException("connection refused") : Status(HttpStatusCode.OK));
        var sink = Create(handler, new() { Url = Api, Retry = Retry(1) });

        var result = await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 1 });

        result.IsSuccess.Should().BeTrue();
        handler.Calls.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NoRetrySection_CallsOnce()
    {
        var handler = new FakeHandler(HttpStatusCode.ServiceUnavailable);
        var sink = Create(handler, new() { Url = Api });

        await WriteAsync(sink, new Dictionary<string, object?> { ["id"] = 1 });

        handler.Calls.Should().ContainSingle();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RateLimit_SpacesRequests()
    {
        var sink = Create(new FakeHandler(), new() { Url = Api, RateLimit = 20 });
        var ct = TestContext.Current.CancellationToken;
        await using var session = (await sink.OpenAsync("u", ct)).Value;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 3; i++)
            await session.WriteAsync(new Dictionary<string, object?> { ["id"] = i }, ct);

        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(90), "3 calls at 20/s need two 50 ms gaps");
    }
}
