using System.Net;

namespace Taskus.Tests.Transports;

/// <summary>Records every request; answers with <paramref name="respond"/>, given the 1-based call number.</summary>
internal sealed class FakeHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
{
    public FakeHandler(HttpStatusCode status = HttpStatusCode.OK, string response = "")
        : this(_ => new HttpResponseMessage(status) { Content = new StringContent(response) })
    {
    }

    public List<(HttpMethod Method, string Url, string? Body, HttpRequestMessage Request)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request.Method, request.RequestUri!.ToString(), body, request));
        return respond(Calls.Count);
    }
}
