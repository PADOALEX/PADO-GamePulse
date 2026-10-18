namespace GamePulse.Desktop.Services;

/// <summary>
/// A fixed set of gates coalesces concurrent requests for one thumbnail through its byte cache.
/// No URL or per-row lock is retained, so scrolling arbitrarily large lists stays bounded.
/// </summary>
internal sealed class ThumbnailRequestGate
{
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async ValueTask<IDisposable> EnterAsync(string url, CancellationToken token)
    {
        var gate = _gates[(uint)StringComparer.Ordinal.GetHashCode(url) % (uint)_gates.Length];
        await gate.WaitAsync(token).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
