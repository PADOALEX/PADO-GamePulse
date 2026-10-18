using GamePulse.Core;

namespace GamePulse.Infrastructure;

/// <summary>
/// Session-only library cache with row and estimated byte budgets shared by owned and recent lists.
/// Identical in-flight queries share transport; cancellation of the final observer cancels that transport.
/// Forced refresh invalidates the entry and prevents an older response from entering the cache.
/// Failed refreshes remain visible and are never replaced with stale cached data.
/// </summary>
public sealed class SteamLibraryService(ISteamLibraryClient steam, SteamOptions options, TimeProvider clock) : ILibraryService
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private long sequence;
    private long cachedBytes;
    private long cachedRows;

    private sealed record Entry(object Value, DateTimeOffset Expires, long Sequence, int Rows, long Bytes);
    private sealed class Pending(object task)
    {
        public object Task { get; } = task;
        public CancellationTokenSource Cancellation { get; } = new();
        public int Observers { get; set; }
        public bool Completed { get; set; }
        public bool Abandoned { get; set; }
    }

    public Task<Outcome<OwnedLibrary>> GetLibraryAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) =>
        CachedAsync("owned:" + steamId, options.LibraryTtl, forceRefresh,
            ct => steam.GetLibraryAsync(steamId, ct), data => data.Games, cancellationToken);

    public Task<Outcome<RecentGames>> GetRecentAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) =>
        CachedAsync("recent:" + steamId, options.RecentTtl, forceRefresh,
            ct => steam.GetRecentAsync(steamId, ct), data => data.Games, cancellationToken);

    private async Task<Outcome<T>> CachedAsync<T>(string key, TimeSpan ttl, bool force,
        Func<CancellationToken, Task<Outcome<T>>> load, Func<T, IReadOnlyList<LibraryGame>> games,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Pending active;
        TaskCompletionSource<Outcome<T>>? completion = null;
        lock (gate)
        {
            if (force) RemoveEntry(key);
            else if (cache.TryGetValue(key, out var hit))
            {
                if (hit.Expires > clock.GetUtcNow()) return ((Outcome<T>)hit.Value) with { FromCache = true };
                RemoveEntry(key);
            }
            if (!force && pending.TryGetValue(key, out var existing) && !existing.Abandoned)
                active = existing;
            else
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                active = new(completion.Task);
                pending[key] = active;
            }
            active.Observers++;
        }
        if (completion is not null) _ = CompleteAsync(key, ttl, load, games, active, completion);
        try
        {
            var result = await ((Task<Outcome<T>>)active.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            bool cancel = false;
            lock (gate)
            {
                if (--active.Observers == 0)
                {
                    if (active.Completed) active.Cancellation.Dispose();
                    else if (!((Task)active.Task).IsCompleted)
                    {
                        active.Abandoned = true;
                        cancel = true;
                    }
                }
            }
            if (cancel)
            {
                // Completion may dispose the source between the lock and cancellation.
                try { active.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    private async Task CompleteAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<Outcome<T>>> load,
        Func<T, IReadOnlyList<LibraryGame>> games, Pending active, TaskCompletionSource<Outcome<T>> completion)
    {
        try
        {
            var result = await load(active.Cancellation.Token).ConfigureAwait(false);
            active.Cancellation.Token.ThrowIfCancellationRequested();
            if (result.HasData() && ttl > TimeSpan.Zero)
            {
                var rows = games(result.Data!);
                // Conservative accounting includes per-record/collection overhead and UTF-16 names.
                var bytes = 256L + rows.Sum(game => 192L + (game.Name?.Length ?? 0) * 2L);
                lock (gate)
                {
                    if (!active.Abandoned && pending.TryGetValue(key, out var latest) && ReferenceEquals(latest, active))
                    {
                        foreach (var expired in cache.Where(x => x.Value.Expires <= clock.GetUtcNow()).Select(x => x.Key).ToArray())
                            RemoveEntry(expired);
                        var rowBudget = Math.Clamp(options.MaxLibraryCacheRows, 0, 100_000);
                        var byteBudget = Math.Clamp(options.MaxLibraryCacheBytes, 0, 64 * 1024 * 1024);
                        var entryBudget = Math.Clamp(options.MaxCacheEntries, 1, 4096);
                        if (rows.Count <= rowBudget && bytes <= byteBudget)
                        {
                            RemoveEntry(key);
                            while (cache.Count > 0 && (cache.Count >= entryBudget ||
                                cachedRows + rows.Count > rowBudget || cachedBytes + bytes > byteBudget))
                                RemoveEntry(cache.MinBy(x => x.Value.Sequence).Key);
                            cache[key] = new(result, clock.GetUtcNow() + ttl, ++sequence, rows.Count, bytes);
                            cachedRows += rows.Count;
                            cachedBytes += bytes;
                        }
                    }
                }
            }
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            // Preserve unexpected programming failures for callers; transport errors are safe Outcomes.
            completion.TrySetException(exception);
        }
        finally
        {
            lock (gate)
            {
                if (pending.TryGetValue(key, out var latest) && ReferenceEquals(latest, active)) pending.Remove(key);
                active.Completed = true;
                if (active.Observers == 0) active.Cancellation.Dispose();
            }
        }
    }

    private void RemoveEntry(string key)
    {
        if (!cache.Remove(key, out var removed)) return;
        cachedRows -= removed.Rows;
        cachedBytes -= removed.Bytes;
    }
}
