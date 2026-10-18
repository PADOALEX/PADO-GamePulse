using System.Net;
using GamePulse.Desktop.Services;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class AvatarServiceTests
{
    private const string Url = "https://avatars.steamstatic.com/example_full.jpg";

    [Theory]
    [InlineData(Url)]
    [InlineData("https://avatars.fastly.steamstatic.com/example.jpg")]
    [InlineData("https://steamcdn-a.akamaihd.net/steamcommunity/public/images/avatars/example.jpg")]
    public void DocumentedCdnAvatarUrlsAreAccepted(string url) => Assert.True(AvatarService.IsAllowedUrl(url));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("http://avatars.steamstatic.com/a.jpg")]
    [InlineData("https://avatars.steamstatic.com.evil.test/a.jpg")]
    [InlineData("https://steamstatic.com/a.jpg")]
    [InlineData("https://avatars.steamstatic.com/a.jpg?key=sentinel")]
    [InlineData("https://avatars.steamstatic.com/a.jpg#fragment")]
    [InlineData("https://user@avatars.steamstatic.com/a.jpg")]
    [InlineData("https://avatars.steamstatic.com:8443/a.jpg")]
    [InlineData(" https://avatars.steamstatic.com/a.jpg")]
    [InlineData("https://avatars.steamstatic.com/a\\b.jpg")]
    public async Task UnsafeUrlsNeverReachTransport(string? url)
    {
        var transport = new Responses(() => throw new InvalidOperationException("Unexpected HTTP"));
        using var avatars = new AvatarService(new HttpClient(transport), TimeProvider.System);
        Assert.False(AvatarService.IsAllowedUrl(url));
        Assert.Null(await avatars.LoadAsync(url, default));
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)] [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)] [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OptionalImageFailureDoesNotRetry(HttpStatusCode status)
    {
        var transport = new Responses(() => new(status));
        using var avatars = new AvatarService(new HttpClient(transport), TimeProvider.System);
        Assert.Null(await avatars.LoadAsync(Url, default));
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task InvalidEncodedDataDoesNotEnterCache()
    {
        var transport = new Responses(() => Image([1, 2, 3]));
        using var avatars = new AvatarService(new HttpClient(transport), TimeProvider.System);
        Assert.Null(await avatars.LoadAsync(Url, default));
        Assert.Null(await avatars.LoadAsync(Url, default));
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task AdvertisedLargePayloadIsRejected()
    {
        var transport = new Responses(() =>
        {
            var response = Image([]);
            response.Content.Headers.ContentLength = 2 * 1024 * 1024 + 1;
            return response;
        });
        using var avatars = new AvatarService(new HttpClient(transport), TimeProvider.System);
        Assert.Null(await avatars.LoadAsync(Url, default));
    }

    [Fact]
    public async Task CancellationAndDisposalDoNotStartNewRequests()
    {
        var transport = new Responses(() => throw new InvalidOperationException("Unexpected HTTP"));
        var avatars = new AvatarService(new HttpClient(transport), TimeProvider.System);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => avatars.LoadAsync(Url, cancel.Token));
        avatars.Dispose(); avatars.Dispose();
        Assert.Null(await avatars.LoadAsync(Url, default));
        Assert.Equal(0, transport.Calls);
    }

    private static HttpResponseMessage Image(byte[] bytes)
    {
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        result.Content.Headers.ContentType = new("image/png"); return result;
    }
    private sealed class Responses(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(response()); }
    }
}
