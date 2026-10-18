using System.Net;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace GamePulse.Desktop.Services;

/// <summary>Key/cookie-free row and selected-item images with bounded transport, decode and memory.</summary>
public sealed class InventoryIconService : IInventoryIconLoader, IDisposable
{
    private const int MaximumImageBytes = 2 * 1024 * 1024, MaximumCacheBytes = 12 * 1024 * 1024, MaximumEntries = 48;
    private readonly HttpClient _client;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _concurrency = new(2, 2);
    private readonly ThumbnailRequestGate _requests = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private long _sequence;
    private int _bytes, _disposed;
    private sealed record Entry(byte[] Bytes, DateTimeOffset Expires, long Sequence);

    public InventoryIconService() : this(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(5)
    }) { Timeout = Timeout.InfiniteTimeSpan }, TimeProvider.System) { }

    public InventoryIconService(HttpClient client, TimeProvider clock) { _client = client; _clock = clock; }

    public static bool IsAllowedUrl(string? value)
    {
        if (value is null || value.Length > 6000 || value.Trim() != value || value.Any(c => char.IsControl(c) || c == '\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.IdnHost != "community.fastly.steamstatic.com" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.StartsWith("/economy/image/", StringComparison.Ordinal)) return false;
        var path = Uri.UnescapeDataString(uri.AbsolutePath["/economy/image/".Length..]);
        return path.Length > 0 && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '+' or '=' or '/');
    }

    public async Task<Bitmap?> LoadAsync(string? url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0 || !IsAllowedUrl(url)) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var operation = deadline.Token;
        try
        {
            // Concurrent row/detail requests share the completed encoded-byte cache,
            // while every consumer retains independent bitmap ownership and cancellation.
            using var request = await _requests.EnterAsync(url!, operation).ConfigureAwait(false);
            await _concurrency.WaitAsync(operation).ConfigureAwait(false);
            try
            {
                byte[]? bytes = null;
                lock (_gate)
                {
                    if (_cache.TryGetValue(url!, out var hit))
                    {
                        if (hit.Expires > _clock.GetUtcNow())
                        { bytes = hit.Bytes; _cache[url!] = hit with { Sequence = ++_sequence }; }
                        else Remove(url!);
                    }
                }
                var fromCache = bytes is not null;
                if (bytes is null)
                {
                    using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, operation).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumImageBytes ||
                        response.Content.Headers.ContentType?.MediaType is not ("image/png" or "image/jpeg" or "image/webp")) return null;
                    await using var stream = await response.Content.ReadAsStreamAsync(operation).ConfigureAwait(false);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[16 * 1024];
                    while (true)
                    {
                        var count = await stream.ReadAsync(chunk, operation).ConfigureAwait(false);
                        if (count == 0) break;
                        if (buffer.Length + count > MaximumImageBytes) return null;
                        buffer.Write(chunk, 0, count);
                    }
                    bytes = buffer.ToArray();
                }
                var decoded = await Task.Run(() => Decode(bytes, operation), operation).ConfigureAwait(false);
                if (decoded is null) return null;
                if (operation.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                { decoded.Dispose(); operation.ThrowIfCancellationRequested(); return null; }
                if (!fromCache)
                {
                    lock (_gate)
                    {
                        if (Volatile.Read(ref _disposed) == 0)
                        {
                            Remove(url!);
                            while (_cache.Count > 0 && (_cache.Count >= MaximumEntries || _bytes + bytes.Length > MaximumCacheBytes))
                                Remove(_cache.MinBy(x => x.Value.Sequence).Key);
                            _cache[url!] = new(bytes, _clock.GetUtcNow().AddMinutes(30), ++_sequence);
                            _bytes += bytes.Length;
                        }
                    }
                }
                return decoded;
            }
            finally { _concurrency.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return null; } // No raw request URI, response or exception reaches UI/logs.
    }

    private static Bitmap? Decode(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > 8192 || info.Height > 8192 || (long)info.Width * info.Height > 16_000_000) return null;
        using var stream = new MemoryStream(bytes, false);
        var bitmap = info.Width >= info.Height ? Bitmap.DecodeToWidth(stream, Math.Min(info.Width, 256))
            : Bitmap.DecodeToHeight(stream, Math.Min(info.Height, 256));
        if (token.IsCancellationRequested) { bitmap.Dispose(); token.ThrowIfCancellationRequested(); }
        return bitmap;
    }
    private void Remove(string key) { if (_cache.Remove(key, out var old)) _bytes -= old.Bytes.Length; }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.Dispose();
        lock (_gate) { _cache.Clear(); _bytes = 0; }
        // A cancelled operation may still release the semaphore during shutdown.
    }
}
