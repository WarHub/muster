using System.Net;
using System.Net.Http;
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
            ct.ThrowIfCancellationRequested();
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    /// <summary>Stream that returns one chunk of bytes, then throws IOException on the next read —
    /// simulates a connection reset / truncated body after headers have already arrived.</summary>
    private sealed class ResetAfterFirstReadStream(byte[] firstChunk) : Stream
    {
        private int _readCount;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_readCount++ == 0)
            {
                var n = Math.Min(buffer.Length, firstChunk.Length);
                firstChunk.AsSpan(0, n).CopyTo(buffer.Span);
                return ValueTask.FromResult(n);
            }
            throw new IOException("connection reset by peer");
        }
    }

    /// <summary>HttpContent whose read stream throws mid-body, after headers/status have already been observed.</summary>
    private sealed class ResetMidBodyContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => CreateStream();

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateStream();

        private static Task<Stream> CreateStream() =>
            Task.FromResult<Stream>(new ResetAfterFirstReadStream(Encoding.UTF8.GetBytes("{\"partial")));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
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

    [Fact]
    public async Task Connection_reset_mid_body_is_graceful()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ResetMidBodyContent() });
        using var client = new NrClient(handler);

        var result = await client.FetchListAsync("3Pbpd", TestContext.Current.CancellationToken);

        Assert.Null(result.Json);
        Assert.NotNull(result.Error);
        Assert.Contains("connection", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Real_caller_cancellation_propagates()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"name":"war horde","army":{}}"""),
        });
        using var client = new NrClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.FetchListAsync("3Pbpd", cts.Token));
    }
}
