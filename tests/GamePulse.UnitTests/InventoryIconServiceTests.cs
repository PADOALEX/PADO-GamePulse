using System.Net;
using System.Net.Http.Headers;
using GamePulse.Desktop.Services;
using Xunit;

namespace GamePulse.UnitTests;

/// <summary>Transport rejection paths need no Avalonia renderer or network.</summary>
public sealed class InventoryIconServiceTests
{
    private const string Url = "https://community.fastly.steamstatic.com/economy/image/opaque_safe-path+token=/256fx256f";
    private const int MaximumBytes = 2 * 1024 * 1024;

    [Theory]
    [InlineData(Url)]
    [InlineData("https://community.fastly.steamstatic.com:443/economy/image/abc")]
    [InlineData("https://COMMUNITY.FASTLY.STEAMSTATIC.COM/economy/image/abc_123-456+/256fx256f")]
    public void AllowsOnlyFixedHttpsCommunityImagePaths(string url) => Assert.True(InventoryIconService.IsAllowedUrl(url));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("not a url")]
    [InlineData("http://community.fastly.steamstatic.com/economy/image/abc")]
    [InlineData("https://community.fastly.steamstatic.com.evil.test/economy/image/abc")]
    [InlineData("https://community.fastly.steamstatic.com./economy/image/abc")]
    [InlineData("https://evil.test/economy/image/abc")]
    [InlineData("https://community.fastly.steamstatic.com:444/economy/image/abc")]
    [InlineData("https://user:password@community.fastly.steamstatic.com/economy/image/abc")]
    [InlineData("https://community.fastly.steamstatic.com/economy/image/abc?key=DO_NOT_EXPOSE")]
    [InlineData("https://community.fastly.steamstatic.com/economy/image/abc?")]
    [InlineData("https://community.fastly.steamstatic.com/economy/image/abc#fragment")]
    [InlineData("https://community.fastly.steamstatic.com/economy/image/")]
    [InlineData("https://community.fastly.steamstatic.com/Economy/image/abc")]
    [InlineData("https://community.fastly.steamstatic.com/economy/image/abc.png")]
    [InlineData("https://community.fastly.steamstatic.com/economy/image/abc%20def")]
    [InlineData("https://community.fastly.steamstatic.com/economy/image/abc%3Fkey=secret")]
    [InlineData("https://community.fastly.steamstatic.com/economy/image/abc\\def")]
    [InlineData("https://community.fastly.steamstatic.com/economy/image/abc\ndef")]
    [InlineData("https://community.fastly.steamstatic.com/economy/image/abc<>def")]
    public async Task BlockedInputNeverReachesHttp(string? url)
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Blocked URL reached HTTP"));
        using var service = new InventoryIconService(new(handler), TimeProvider.System);
        Assert.False(InventoryIconService.IsAllowedUrl(url));
        Assert.Null(await service.LoadAsync(url, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task OversizedUrlIsRejectedBeforeHttp()
    {
        var url = "https://community.fastly.steamstatic.com/economy/image/" + new string('a', 6000);
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var service = new InventoryIconService(new(handler), TimeProvider.System);
        Assert.False(InventoryIconService.IsAllowedUrl(url)); Assert.Null(await service.LoadAsync(url, default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)] [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)] [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task FailedResponsesHaveNoHiddenRetries(HttpStatusCode status)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var service = new InventoryIconService(new(handler), TimeProvider.System);
        Assert.Null(await service.LoadAsync(Url, default)); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(null)] [InlineData("text/html")] [InlineData("application/json")] [InlineData("image/gif")]
    public async Task MissingOrUnsupportedMediaTypeIsRejectedWithoutReadingPayload(string? mediaType)
    {
        var stream = new CountingStream(new byte[32]);
        var handler = new FakeHandler((_, _) => Task.FromResult(Response(stream, mediaType)));
        using var service = new InventoryIconService(new(handler), TimeProvider.System);
        Assert.Null(await service.LoadAsync(Url, default)); Assert.Equal(0, stream.BytesRead); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AdvertisedOversizeIsRejectedWithoutReadingPayload()
    {
        var stream = new CountingStream(new byte[32]);
        var handler = new FakeHandler((_, _) =>
        {
            var response = Response(stream, "image/png");
            response.Content.Headers.ContentLength = MaximumBytes + 1L;
            return Task.FromResult(response);
        });
        using var service = new InventoryIconService(new(handler), TimeProvider.System);
        Assert.Null(await service.LoadAsync(Url, default)); Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task ChunkedStreamOversizeStopsAtTheBoundWithoutDecoding()
    {
        var stream = new CountingStream(new byte[MaximumBytes + 100_000]);
        var handler = new FakeHandler((_, _) => Task.FromResult(Response(stream, "image/png")));
        using var service = new InventoryIconService(new(handler), TimeProvider.System);
        Assert.Null(await service.LoadAsync(Url, default));
        Assert.InRange(stream.BytesRead, MaximumBytes + 1, MaximumBytes + 16 * 1024);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("image/png")] [InlineData("image/jpeg")] [InlineData("image/webp")]
    public async Task MalformedAllowedImageReturnsNullAndDoesNotBecomeACacheHit(string mediaType)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Response(new CountingStream([1, 2, 3, 4]), mediaType)));
        using var service = new InventoryIconService(new(handler), TimeProvider.System);
        Assert.Null(await service.LoadAsync(Url, default)); Assert.Null(await service.LoadAsync(Url, default));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CancelledCallerThrowsBeforeHttpEvenForAnInvalidUrl()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var service = new InventoryIconService(new(handler), TimeProvider.System);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync(Url, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync("not a url", cancelled.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task InFlightCancellationReachesTransportAndDoesNotReturnAFalseSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async (_, token) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        using var service = new InventoryIconService(new(handler), TimeProvider.System);
        using var cancellation = new CancellationTokenSource(); var loading = service.LoadAsync(Url, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TransportExceptionContainingASecretNeverEscapes()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("https://evil.test/?key=DO_NOT_EXPOSE"));
        using var service = new InventoryIconService(new(handler), TimeProvider.System);
        Assert.Null(await service.LoadAsync(Url, default)); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DisposedServiceStartsNoNewRequest()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        var service = new InventoryIconService(new(handler), TimeProvider.System); service.Dispose(); service.Dispose();
        Assert.Null(await service.LoadAsync(Url, default)); Assert.Equal(0, handler.Calls);
    }

    private static HttpResponseMessage Response(Stream stream, string? mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        if (mediaType is not null) response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }
    private sealed class CountingStream(byte[] bytes) : Stream
    {
        private int position;
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, bytes.Length - position); bytes.AsSpan(position, read).CopyTo(buffer.AsSpan(offset, read));
            position += read; BytesRead += read; return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); var read = Math.Min(buffer.Length, bytes.Length - position);
            bytes.AsSpan(position, read).CopyTo(buffer.Span); position += read; BytesRead += read; return ValueTask.FromResult(read);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
