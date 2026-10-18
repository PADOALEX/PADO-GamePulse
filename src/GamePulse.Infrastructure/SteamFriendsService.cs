using GamePulse.Core;

namespace GamePulse.Infrastructure;

/// <summary>
/// Retains all validated relationships, enriches summaries in documented batches and caches only complete results.
/// Identical pending observers share transport; the last cancellation cancels it. A forced refresh supersedes
/// older cache writers without hiding refresh errors behind stale data. No friends data is persisted.
/// </summary>
public sealed class SteamFriendsService(ISteamFriendsClient steam, SteamOptions options, TimeProvider clock) : IFriendsService
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private long sequence;
    private long cachedBytes;
    private long cachedRows;

    private sealed record Entry(Outcome<FriendDirectory> Value, DateTimeOffset Expires, long Sequence, int Rows, long Bytes);
    private sealed class Pending(Task<Outcome<FriendDirectory>> task)
    {
        public Task<Outcome<FriendDirectory>> Task { get; } = task;
        public CancellationTokenSource Cancellation { get; } = new();
        public int Observers { get; set; }
        public bool Completed { get; set; }
        public bool Abandoned { get; set; }
    }

    public async Task<Outcome<FriendDirectory>> GetFriendsAsync(string steamId, bool forceRefresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ProfileInput.Parse(steamId).Data is not { Kind: ProfileInputKind.SteamId } id || id.Value != steamId)
            return Outcome<FriendDirectory>.Failure(DataState.InvalidInput, "Enter a valid public individual SteamID64.");
        Pending active;
        TaskCompletionSource<Outcome<FriendDirectory>>? completion = null;
        lock (gate)
        {
            if (forceRefresh) RemoveEntry(steamId);
            else if (cache.TryGetValue(steamId, out var hit))
            {
                if (hit.Expires > clock.GetUtcNow()) return hit.Value with { FromCache = true };
                RemoveEntry(steamId);
            }
            if (!forceRefresh && pending.TryGetValue(steamId, out var existing) && !existing.Abandoned)
                active = existing;
            else
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                active = new(completion.Task);
                pending[steamId] = active;
            }
            active.Observers++;
        }
        if (completion is not null) _ = CompleteAsync(steamId, active, completion);
        try
        {
            var result = await active.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                    else if (!active.Task.IsCompleted)
                    {
                        active.Abandoned = true;
                        cancel = true;
                    }
                }
            }
            if (cancel)
            {
                try { active.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    private async Task<Outcome<FriendDirectory>> LoadAsync(string steamId, CancellationToken cancellationToken)
    {
        var list = await steam.GetFriendListAsync(steamId, cancellationToken).ConfigureAwait(false);
        if (list.Data is null || list.State is not (DataState.Ready or DataState.Empty))
            return Outcome<FriendDirectory>.Failure(list.State, list.Message ?? "Steam friends could not be loaded.");
        if (list.Data.Count == 0)
            return new(DataState.Empty, new(Array.Empty<SteamFriend>(), true, 0),
                "Steam explicitly returned no friends.", list.UpdatedAt ?? clock.GetUtcNow());

        var friends = list.Data.ToArray();
        var failedBatches = 0;
        var batches = Enumerable.Range(0, (friends.Length + SteamApiClient.PlayerSummaryBatchSize - 1) /
            SteamApiClient.PlayerSummaryBatchSize);
        await Parallel.ForEachAsync(batches, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(options.MaxConcurrentFriendBatches, 1, 2),
            CancellationToken = cancellationToken
        }, async (batch, ct) =>
        {
            var start = batch * SteamApiClient.PlayerSummaryBatchSize;
            var length = Math.Min(SteamApiClient.PlayerSummaryBatchSize, friends.Length - start);
            var ids = friends.AsSpan(start, length).ToArray().Select(friend => friend.SteamId).ToArray();
            var summaries = await steam.GetPlayerSummariesAsync(ids, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!summaries.IsReady)
            {
                Interlocked.Increment(ref failedBatches);
                for (var offset = 0; offset < length; offset++)
                    friends[start + offset] = friends[start + offset] with { Profile = null, SummaryState = summaries.State };
                return;
            }
            var profiles = summaries.Data!.ToDictionary(profile => profile.SteamId, StringComparer.Ordinal);
            for (var offset = 0; offset < length; offset++)
            {
                var friend = friends[start + offset];
                friends[start + offset] = profiles.TryGetValue(friend.SteamId, out var profile)
                    ? friend with { Profile = profile, SummaryState = DataState.Ready }
                    : friend with { Profile = null, SummaryState = DataState.Unavailable };
            }
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var complete = friends.All(friend => friend.Profile is not null && friend.SummaryState == DataState.Ready);
        return new(DataState.Ready, new(Array.AsReadOnly(friends), complete, failedBatches),
            complete ? null : "All available friend IDs are shown. Some profile summaries could not be retrieved; refresh to retry. Missing summaries do not mean offline or private.",
            clock.GetUtcNow());
    }

    private async Task CompleteAsync(string steamId, Pending active, TaskCompletionSource<Outcome<FriendDirectory>> completion)
    {
        try
        {
            var result = await LoadAsync(steamId, active.Cancellation.Token).ConfigureAwait(false);
            active.Cancellation.Token.ThrowIfCancellationRequested();
            if (result.Data is { SummariesComplete: true } data &&
                result.State is DataState.Ready or DataState.Empty && options.FriendsTtl > TimeSpan.Zero)
            {
                var rows = data.Friends;
                var bytes = 256L + rows.Sum(EstimatedBytes);
                lock (gate)
                {
                    if (!active.Abandoned && pending.TryGetValue(steamId, out var latest) && ReferenceEquals(latest, active))
                    {
                        foreach (var expired in cache.Where(x => x.Value.Expires <= clock.GetUtcNow()).Select(x => x.Key).ToArray())
                            RemoveEntry(expired);
                        var rowBudget = Math.Clamp(options.MaxFriendsCacheRows, 0, 100_000);
                        var byteBudget = Math.Clamp(options.MaxFriendsCacheBytes, 0, 64 * 1024 * 1024);
                        var entryBudget = Math.Clamp(options.MaxCacheEntries, 1, 4096);
                        if (rows.Count <= rowBudget && bytes <= byteBudget)
                        {
                            RemoveEntry(steamId);
                            while (cache.Count > 0 && (cache.Count >= entryBudget ||
                                cachedRows + rows.Count > rowBudget || cachedBytes + bytes > byteBudget))
                                RemoveEntry(cache.MinBy(x => x.Value.Sequence).Key);
                            cache[steamId] = new(result, clock.GetUtcNow() + options.FriendsTtl,
                                ++sequence, rows.Count, bytes);
                            cachedRows += rows.Count;
                            cachedBytes += bytes;
                        }
                    }
                }
            }
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
        catch (Exception exception) { completion.TrySetException(exception); }
        finally
        {
            lock (gate)
            {
                if (pending.TryGetValue(steamId, out var latest) && ReferenceEquals(latest, active)) pending.Remove(steamId);
                active.Completed = true;
                if (active.Observers == 0) active.Cancellation.Dispose();
            }
        }
    }

    private static long EstimatedBytes(SteamFriend friend)
    {
        var profile = friend.Profile;
        return 384L + friend.SteamId.Length * 2L + (profile?.DisplayName?.Length ?? 0) * 2L +
            (profile?.ProfileUrl.Length ?? 0) * 2L + (profile?.AvatarUrl?.Length ?? 0) * 2L +
            (profile?.CurrentGame?.Length ?? 0) * 2L;
    }

    private void RemoveEntry(string steamId)
    {
        if (!cache.Remove(steamId, out var removed)) return;
        cachedRows -= removed.Rows;
        cachedBytes -= removed.Bytes;
    }
}
