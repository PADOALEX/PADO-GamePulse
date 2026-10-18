using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GamePulse.Desktop.Services;

namespace GamePulse.UiChecks;

internal static class AvatarImageChecks
{
    public static async Task RunAsync()
    {
        const string url = "https://avatars.steamstatic.com/test-only.jpg";
        var clock = new ImageClock();
        var transport = new ImageResponses(Encode(24, 384));
        using var avatars = new AvatarService(new HttpClient(transport), clock);
        using (var first = await avatars.LoadAsync(url, default))
        using (var second = await avatars.LoadAsync(url, default))
        {
            Check(first is not null && second is not null && !ReferenceEquals(first, second), "Avatar cache must return independent owned bitmaps.");
            Check(first!.PixelSize.Height <= 128 && first.PixelSize.Width <= 128, "Both avatar axes must be bounded after decode.");
            Check(transport.Calls == 1, "A repeated avatar must use the encoded-byte cache.");
        }
        clock.Advance(TimeSpan.FromMinutes(31));
        using (var expired = await avatars.LoadAsync(url, default)) Check(expired is not null && transport.Calls == 2, "Avatar TTL must expire.");
        for (var i = 0; i < 49; i++)
        { using var image = await avatars.LoadAsync($"https://avatars.steamstatic.com/test-{i}.jpg", default); Check(image is not null, "Valid fixture avatar must decode."); }
        var calls = transport.Calls;
        using (var evicted = await avatars.LoadAsync(url, default)) Check(evicted is not null && transport.Calls == calls + 1, "Avatar entry budget must evict least-recent entries.");
        transport.Bytes = Encode(1, 10000);
        using (var tooTall = await avatars.LoadAsync("https://avatars.steamstatic.com/tall.jpg", default))
            Check(tooTall is null, "Avatar source dimensions must be bounded before bitmap decode.");
        transport.Bytes = Encode(4, 4);
        using (var retry = await avatars.LoadAsync("https://avatars.steamstatic.com/tall.jpg", default))
            Check(retry is not null, "A rejected avatar must not poison the cache.");
        await ConcurrentThumbnailChecksAsync(Encode(16, 16));
        Console.WriteLine("Avatar image checks passed: dimensions, owned bitmaps, TTL, eviction, rejected-cache recovery, concurrent thumbnail deduplication and independent cancellation.");
    }

    private static async Task ConcurrentThumbnailChecksAsync(byte[] bytes)
    {
        foreach (var inventory in new[] { false, true })
        {
            var transport = new DeferredImageResponses(bytes);
            using var client = new HttpClient(transport);
            using var lifetime = inventory
                ? (IDisposable)new InventoryIconService(client, TimeProvider.System)
                : new AvatarService(client, TimeProvider.System);
            var loader = (IThumbnailLoader)lifetime;
            var url = inventory ? "https://community.fastly.steamstatic.com/economy/image/test-only"
                : "https://avatars.steamstatic.com/test-only.jpg";
            var first = loader.LoadAsync(url, default);
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var second = loader.LoadAsync(url, default);
            using var cancelled = new CancellationTokenSource();
            var waiter = loader.LoadAsync(url, cancelled.Token);
            Check(transport.Calls == 1, "Concurrent row/detail thumbnail consumers must not send duplicate HTTP requests.");
            cancelled.Cancel();
            try { await waiter; throw new InvalidOperationException("Cancelled thumbnail waiter completed successfully."); }
            catch (OperationCanceledException) { }
            Check(!first.IsCompleted && !second.IsCompleted, "A cancelled thumbnail waiter must leave other consumers active.");
            transport.Release.SetResult();
            using var firstImage = await first.WaitAsync(TimeSpan.FromSeconds(3));
            using var secondImage = await second.WaitAsync(TimeSpan.FromSeconds(3));
            Check(firstImage is not null && secondImage is not null && !ReferenceEquals(firstImage, secondImage),
                "Coalesced thumbnail requests must still return independently owned decoded images.");
            Check(transport.Calls == 1, "The second simultaneous consumer must use the cache after the first finishes.");
        }
    }

    private static byte[] Encode(int width, int height)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using var bytes = new MemoryStream();
        var sample = new Border { Width = width, Height = height, Background = Brushes.MediumAquamarine };
        sample.Measure(new Size(width, height)); sample.Arrange(new Rect(0, 0, width, height)); bitmap.Render(sample);
        bitmap.Save(bytes, PngBitmapEncoderOptions.Default); return bytes.ToArray();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class ImageClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
    private sealed class ImageResponses(byte[] bytes) : HttpMessageHandler
    {
        public byte[] Bytes { get; set; } = bytes;
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            Check(request.Headers.Authorization is null && !request.Headers.Contains("Cookie") && request.RequestUri!.Query.Length == 0,
                "Avatar requests must remain key/cookie free.");
            var result = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
            result.Content.Headers.ContentType = new("image/png"); return Task.FromResult(result);
        }
    }
    private sealed class DeferredImageResponses(byte[] bytes) : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            var result = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            result.Content.Headers.ContentType = new("image/png");
            return result;
        }
    }
}
