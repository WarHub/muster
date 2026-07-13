using System.Net;
using System.Net.Http;
using System.Text;
using Muster.Cli.Reports;
using Xunit;

namespace Muster.Cli.Tests.Reports;

public class AttachmentClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public int InvocationCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            InvocationCount++;
            LastRequest = request;
            return Task.FromResult(respond(request));
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
            Task.FromResult<Stream>(new ResetAfterFirstReadStream(Encoding.UTF8.GetBytes("PK\x03\x04partial")));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private const string ValidUrl = "https://github.com/user-attachments/files/12345/my-list.rosz";

    [Fact]
    public async Task Download_returns_bytes_on_success()
    {
        var payload = Encoding.UTF8.GetBytes("roster bytes");
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        using var client = new AttachmentClient(handler);

        var (data, error) = await client.DownloadAsync(ValidUrl, TestContext.Current.CancellationToken);

        Assert.Null(error);
        Assert.Equal(payload, data);
        Assert.Equal(1, handler.InvocationCount);
        Assert.Equal(ValidUrl, handler.LastRequest!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData("https://evil.example/x.rosz")]
    [InlineData("https://github.com/user-attachments/files/12345/../../etc/passwd")]
    [InlineData("http://github.com/user-attachments/files/12345/my-list.rosz")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Url_revalidation_rejects_non_allowlisted_urls_without_any_http_call(string url)
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK));
        using var client = new AttachmentClient(handler);

        var (data, error) = await client.DownloadAsync(url, TestContext.Current.CancellationToken);

        Assert.Null(data);
        Assert.NotNull(error);
        Assert.Equal(0, handler.InvocationCount);
    }

    [Fact]
    public async Task Legacy_owner_repo_attachment_url_is_accepted()
    {
        var payload = Encoding.UTF8.GetBytes("roster bytes");
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        using var client = new AttachmentClient(handler);

        var (data, error) = await client.DownloadAsync(
            "https://github.com/BSData/wh40k-11e/files/9999/army.ros", TestContext.Current.CancellationToken);

        Assert.Null(error);
        Assert.Equal(payload, data);
    }

    [Fact]
    public async Task Http_error_is_graceful()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.NotFound));
        using var client = new AttachmentClient(handler);

        var (data, error) = await client.DownloadAsync(ValidUrl, TestContext.Current.CancellationToken);

        Assert.Null(data);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Oversized_response_is_rejected()
    {
        var big = new byte[6 * 1024 * 1024];
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(big) });
        using var client = new AttachmentClient(handler);

        var (data, error) = await client.DownloadAsync(ValidUrl, TestContext.Current.CancellationToken);

        Assert.Null(data);
        Assert.Contains("too large", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_reset_mid_body_is_graceful()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ResetMidBodyContent() });
        using var client = new AttachmentClient(handler);

        var (data, error) = await client.DownloadAsync(ValidUrl, TestContext.Current.CancellationToken);

        Assert.Null(data);
        Assert.NotNull(error);
        Assert.Contains("connection", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Real_caller_cancellation_propagates()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("data")),
        });
        using var client = new AttachmentClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.DownloadAsync(ValidUrl, cts.Token));
    }
}
