using System.Net;
using System.Text;
using Muster.Cli.NewRecruit;
using Xunit;

namespace Muster.Cli.Tests.NewRecruit;

public class NrClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    [Fact]
    public async Task Fetch_posts_open_share_link_rpc_and_returns_json()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"name":"war horde","army":{}}""", Encoding.UTF8, "application/json"),
        });
        using var client = new NrClient(handler);

        var result = await client.FetchListAsync("3Pbpd", TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Contains("war horde", result.Json, StringComparison.Ordinal);
        Assert.Equal("https://www.newrecruit.eu/api/rpc", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"open_share_link\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"3Pbpd\"", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_rpc_response_is_not_found()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("null") });
        using var client = new NrClient(handler);
        var result = await client.FetchListAsync("gone1", TestContext.Current.CancellationToken);
        Assert.Null(result.Json);
        Assert.Contains("no longer shared", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_error_is_graceful()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.InternalServerError));
        using var client = new NrClient(handler);
        var result = await client.FetchListAsync("3Pbpd", TestContext.Current.CancellationToken);
        Assert.Null(result.Json);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Oversized_response_is_rejected()
    {
        var big = new string('x', 6 * 1024 * 1024);
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent($"{{\"pad\":\"{big}\"}}") });
        using var client = new NrClient(handler);
        var result = await client.FetchListAsync("3Pbpd", TestContext.Current.CancellationToken);
        Assert.Null(result.Json);
        Assert.Contains("too large", result.Error, StringComparison.Ordinal);
    }
}
