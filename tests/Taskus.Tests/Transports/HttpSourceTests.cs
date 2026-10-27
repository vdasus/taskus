using System.Net;
using FluentAssertions;
using Serilog.Core;
using Taskus.Config;
using Taskus.Mapping;
using Taskus.Pipeline;
using Taskus.Transports;

namespace Taskus.Tests.Transports;

public class HttpSourceTests
{
    private static ISource Create(FakeHandler handler, Dictionary<string, object?> settings) =>
        HttpSource.Create(Settings.Of("source", settings), Logger.None, handler).Value;

    private static async Task<List<SourceRow>> ReadAsync(ISource source) =>
        await source.Units().Single().Rows.ToListAsync(TestContext.Current.CancellationToken);

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public async Task ArrayAtItemsPath_GivesOneRowPerObject()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"data":{"items":[{"id":1,"name":"Ann","amount":2.5},{"id":2,"name":null}]}}""");
        var source = Create(handler, new() { ["url"] = "http://example.com/api/clients", ["items"] = "data.items" });

        var rows = await ReadAsync(source);

        rows.Select(r => r.Number).Should().Equal(1, 2);
        rows[0].Values.Should().BeEquivalentTo(new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "Ann", ["amount"] = 2.5m });
        rows[1].Values["name"].Should().BeNull();
        handler.Calls.Single().Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ArrayOfScalars_GivesValueField()
    {
        var source = Create(new FakeHandler(HttpStatusCode.OK, "[101, 102]"), new() { ["url"] = "http://example.com/api/ids" });

        var rows = await ReadAsync(source);

        rows.Select(r => r.Values["value"]).Should().Equal(101L, 102L);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NestedObject_IsReachableFromMapping()
    {
        var source = Create(new FakeHandler(HttpStatusCode.OK, """{"id":1,"address":{"city":"Vilnius"}}"""), new() { ["url"] = "http://example.com/api/one" });
        var mapper = RowMapper.Create([KeyValuePair.Create("city", "{{ row.address.city }}")]).Value;

        var row = (await ReadAsync(source)).Single();

        mapper.Map(row.Values).Value["city"].Should().Be("Vilnius");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PostBody_IsSentAsJson()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var source = Create(handler, new()
        {
            ["url"] = "http://example.com/api/search",
            ["method"] = "POST",
            ["body"] = """{"from":"{{ date.now | date.to_string '%Y' }}"}""",
        });

        (await ReadAsync(source)).Should().BeEmpty();

        var call = handler.Calls.Single();
        call.Method.Should().Be(HttpMethod.Post);
        call.Body.Should().Be($$"""{"from":"{{DateTime.Now.Year}}"}""");
        call.Request.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MissingItemsPath_FailsTheUnit()
    {
        var source = Create(new FakeHandler(HttpStatusCode.OK, """{"data":[]}"""), new() { ["url"] = "http://example.com/api", ["items"] = "result" });

        var read = () => ReadAsync(source);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("source.items 'result' is not in the response");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ErrorStatus_FailsTheUnit()
    {
        var source = Create(new FakeHandler(HttpStatusCode.Unauthorized, "bad token"), new() { ["url"] = "http://example.com/api" });

        var read = () => ReadAsync(source);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("GET http://example.com/api: HTTP 401 Unauthorized bad token");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BodyWithGet_IsInvalid()
    {
        var result = HttpSource.Create(Settings.Of("source", new Dictionary<string, object?> { ["url"] = "http://x", ["body"] = "{}" }), Logger.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("source.body needs method POST, PUT or PATCH");
    }
}
