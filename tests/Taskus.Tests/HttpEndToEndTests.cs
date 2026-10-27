using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using MiniExcelLibs;
using Serilog.Core;

namespace Taskus.Tests;

public sealed class HttpEndToEndTests : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly ConcurrentQueue<string> received = new();
    private readonly string baseUrl;

    public HttpEndToEndTests()
    {
        baseUrl = $"http://localhost:{FreePort()}/";
        listener.Prefixes.Add(baseUrl);
        listener.Start();
        _ = ServeAsync();
    }

    // Answers 500 for a body that carries id 2, 200 otherwise.
    private async Task ServeAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            received.Enqueue($"{context.Request.HttpMethod} {context.Request.Url!.AbsolutePath} {body}");
            context.Response.StatusCode = body.Contains("\"id\":2") ? 500 : 200;
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Priority", "High")]
    public async Task Run_ExcelIds_PostsOneCallPerIdAndReportsFailedCall()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.File("in"));
        MiniExcel.SaveAs(temp.File("in", "ids.xlsx"), new[] { 1, 2, 3 }.Select(id => new Dictionary<string, object?> { ["Id"] = id }));
        var task = temp.Write("task.yaml", $$$"""
            source: { type: excel, folder: {{{temp.File("in")}}} }
            map:
              id: "{{ row.Id }}"
            sink:
              type: http
              method: POST
              url: {{{baseUrl}}}api
            onError: continue
            report: { folder: {{{temp.File("reports")}}}, when: always }
            """);

        var exit = await new App(Logger.None).ExecuteAsync("run", task, "none.yaml", dryRun: false, TestContext.Current.CancellationToken);

        exit.Should().Be(ExitCode.Errors);
        received.Should().Equal("POST /api {\"id\":1}", "POST /api {\"id\":2}", "POST /api {\"id\":3}");
        var report = Directory.GetFiles(temp.File("reports")).Single();
        var error = MiniExcel.Query(report, useHeaderRow: true, sheetName: "Errors").Cast<IDictionary<string, object?>>().Single();
        Convert.ToInt32(error["Row"]).Should().Be(3);
        error["Message"].Should().Be($"POST {baseUrl}api: HTTP 500 Internal Server Error");
    }

    public void Dispose() => listener.Close();
}
