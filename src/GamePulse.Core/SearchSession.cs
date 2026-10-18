namespace GamePulse.Core;

/// <summary>UI-thread-owned generation guard; protects against transports ignoring cancellation.</summary>
public sealed class SearchSession : IDisposable
{
    private CancellationTokenSource? _source;
    private long _generation;
    public (long Generation, CancellationToken Token) Begin()
    {
        Cancel();
        _source = new CancellationTokenSource();
        return (_generation, _source.Token);
    }
    public bool IsCurrent(long generation) => generation == _generation && _source is { IsCancellationRequested: false };
    public void Cancel()
    {
        _generation++;
        _source?.Cancel();
        _source?.Dispose();
        _source = null;
    }
    public void Dispose() => Cancel();
}
