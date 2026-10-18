using GamePulse.Core;

namespace GamePulse.Infrastructure;

/// <summary>Only an explicitly requested game is loaded; no library-wide background sweep occurs.</summary>
public sealed class SteamAchievementService : IAchievementService
{
    private readonly ISteamAchievementClient steam;
    private readonly TimeProvider clock;
    private readonly AchievementOutcomeCache<uint, AchievementSchema> schemas;
    private readonly AchievementOutcomeCache<(string SteamId, uint AppId), GameAchievements> players;

    public SteamAchievementService(ISteamAchievementClient steam, SteamOptions options, TimeProvider clock)
    {
        this.steam = steam;
        this.clock = clock;
        schemas = new(clock, options.AchievementSchemaTtl, options.MaxCacheEntries,
            options.MaxAchievementCacheRows, options.MaxAchievementCacheBytes,
            value => value.State is DataState.Ready or DataState.NoAchievements && value.Data is not null,
            value => (value.Achievements.Count, 256L + (value.GameName?.Length ?? 0) * 2L + value.Achievements.Sum(x =>
                128L + x.ApiName.Length * 2L + (x.DisplayName?.Length ?? 0) * 2L + (x.Description?.Length ?? 0) * 2L)));
        players = new(clock, options.PlayerAchievementsTtl, options.MaxCacheEntries,
            options.MaxAchievementCacheRows, options.MaxAchievementCacheBytes,
            value => value.State is DataState.Ready or DataState.NoAchievements && value.Data?.IsComplete == true,
            value => (value.Achievements.Count, 256L + (value.GameName?.Length ?? 0) * 2L + value.Achievements.Sum(x =>
                160L + x.ApiName.Length * 2L + (x.DisplayName?.Length ?? 0) * 2L + (x.Description?.Length ?? 0) * 2L)));
    }

    public Task<Outcome<GameAchievements>> GetAchievementsAsync(string steamId, uint appId, bool forceRefresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ProfileInput.Parse(steamId).Data is not { Kind: ProfileInputKind.SteamId } id || id.Value != steamId || appId == 0)
            return Task.FromResult(Outcome<GameAchievements>.Failure(DataState.InvalidInput,
                "Enter a valid public individual SteamID64 and a positive Steam AppID."));
        return players.GetAsync((steamId, appId), forceRefresh, cancellationToken,
            ct => LoadAsync(steamId, appId, forceRefresh, ct));
    }

    private async Task<Outcome<GameAchievements>> LoadAsync(string steamId, uint appId, bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var schema = await schemas.GetAsync(appId, forceRefresh, cancellationToken,
            ct => steam.GetAchievementSchemaAsync(appId, ct)).ConfigureAwait(false);
        if (schema.Data is null || schema.State is not (DataState.Ready or DataState.NoAchievements))
            return Outcome<GameAchievements>.Failure(schema.State, schema.Message ?? "Steam achievement metadata could not be loaded.");
        if (schema.Data.AppId != appId) return Malformed();
        if (schema.State == DataState.NoAchievements && schema.Data.Achievements.Count == 0)
            return new(DataState.NoAchievements, new(appId, schema.Data.GameName, Array.Empty<AchievementDetail>(), true),
                "Steam explicitly returned an empty achievement schema.", schema.UpdatedAt ?? clock.GetUtcNow());
        if (schema.Data.Achievements.Count == 0) return Malformed();
        var stats = await steam.GetPlayerAchievementsAsync(steamId, appId, cancellationToken).ConfigureAwait(false);
        if (!stats.IsReady)
            return Outcome<GameAchievements>.Failure(stats.State, stats.Message ?? "Player achievements could not be loaded.");
        if (stats.Data!.AppId != appId) return Malformed();
        cancellationToken.ThrowIfCancellationRequested();
        var byName = stats.Data.Achievements.ToDictionary(x => x.ApiName, StringComparer.Ordinal);
        var definitions = schema.Data.Achievements.ToDictionary(x => x.ApiName, StringComparer.Ordinal);
        var details = new List<AchievementDetail>(Math.Max(definitions.Count, byName.Count));
        foreach (var definition in schema.Data.Achievements)
        {
            byName.TryGetValue(definition.ApiName, out var player);
            details.Add(new(definition.ApiName, definition.DisplayName, definition.Description, definition.Hidden,
                player?.Unlocked, player?.UnlockedAt));
        }
        foreach (var player in stats.Data.Achievements)
            if (!definitions.ContainsKey(player.ApiName))
                details.Add(new(player.ApiName, null, null, null, player.Unlocked, player.UnlockedAt));
        var complete = definitions.Count == byName.Count && definitions.Keys.All(byName.ContainsKey);
        return new(DataState.Ready, new(appId, schema.Data.GameName, details.AsReadOnly(), complete),
            complete ? null : "Steam's schema and player achievement lists differ. Every returned achievement is shown; completion is unavailable until the sources agree.",
            stats.UpdatedAt ?? clock.GetUtcNow());
    }

    private static Outcome<GameAchievements> Malformed() => Outcome<GameAchievements>.Failure(DataState.Failed,
        "Steam returned inconsistent achievement data. Try refreshing later.");
}

/// <summary>Bounded, session-only outcomes; in-flight observers share work and cancel it when the last leaves.</summary>
internal sealed class AchievementOutcomeCache<TKey, TValue>(TimeProvider clock, TimeSpan ttl, int maximumEntries,
    int maximumRows, long maximumBytes, Func<Outcome<TValue>, bool> cacheable,
    Func<TValue, (int Rows, long Bytes)> measure) where TKey : notnull
{
    private readonly object gate = new();
    private readonly Dictionary<TKey, Entry> cache = new();
    private readonly Dictionary<TKey, Pending> pending = new();
    private long sequence;
    private long cachedRows;
    private long cachedBytes;
    private sealed record Entry(Outcome<TValue> Value, DateTimeOffset Expires, long Sequence, int Rows, long Bytes);
    private sealed class Pending(Task<Outcome<TValue>> task)
    {
        public Task<Outcome<TValue>> Task { get; } = task;
        public CancellationTokenSource Cancellation { get; } = new();
        public int Observers { get; set; }
        public bool Completed { get; set; }
        public bool Abandoned { get; set; }
    }

    public async Task<Outcome<TValue>> GetAsync(TKey key, bool forceRefresh, CancellationToken cancellationToken,
        Func<CancellationToken, Task<Outcome<TValue>>> load)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Pending active;
        TaskCompletionSource<Outcome<TValue>>? completion = null;
        lock (gate)
        {
            if (forceRefresh) Remove(key);
            else if (cache.TryGetValue(key, out var hit))
            {
                if (hit.Expires > clock.GetUtcNow()) return hit.Value with { FromCache = true };
                Remove(key);
            }
            if (!forceRefresh && pending.TryGetValue(key, out var existing) && !existing.Abandoned) active = existing;
            else
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                active = new(completion.Task);
                pending[key] = active;
            }
            active.Observers++;
        }
        if (completion is not null) _ = CompleteAsync(key, active, completion, load);
        try
        {
            var result = await active.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            var cancel = false;
            lock (gate)
            {
                if (--active.Observers == 0)
                {
                    if (active.Completed) active.Cancellation.Dispose();
                    else if (!active.Task.IsCompleted) { active.Abandoned = true; cancel = true; }
                }
            }
            if (cancel)
            {
                try { active.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    private async Task CompleteAsync(TKey key, Pending active, TaskCompletionSource<Outcome<TValue>> completion,
        Func<CancellationToken, Task<Outcome<TValue>>> load)
    {
        try
        {
            var value = await load(active.Cancellation.Token).ConfigureAwait(false);
            active.Cancellation.Token.ThrowIfCancellationRequested();
            if (cacheable(value) && value.Data is not null && ttl > TimeSpan.Zero)
            {
                var (rows, bytes) = measure(value.Data);
                lock (gate)
                {
                    // A newer forced refresh owns the cache slot, including when that refresh fails.
                    if (!active.Abandoned && pending.TryGetValue(key, out var latest) && ReferenceEquals(latest, active))
                    {
                        foreach (var expired in cache.Where(x => x.Value.Expires <= clock.GetUtcNow()).Select(x => x.Key).ToArray()) Remove(expired);
                        var rowBudget = Math.Clamp(maximumRows, 0, 100_000);
                        var byteBudget = Math.Clamp(maximumBytes, 0, 64 * 1024 * 1024);
                        var entryBudget = Math.Clamp(maximumEntries, 1, 4096);
                        if (rows <= rowBudget && bytes <= byteBudget)
                        {
                            Remove(key);
                            while (cache.Count > 0 && (cache.Count >= entryBudget || cachedRows + rows > rowBudget || cachedBytes + bytes > byteBudget))
                                Remove(cache.MinBy(x => x.Value.Sequence).Key);
                            cache[key] = new(value, clock.GetUtcNow() + ttl, ++sequence, rows, bytes);
                            cachedRows += rows;
                            cachedBytes += bytes;
                        }
                    }
                }
            }
            completion.TrySetResult(value);
        }
        catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
        catch (Exception exception) { completion.TrySetException(exception); }
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

    private void Remove(TKey key)
    {
        if (!cache.Remove(key, out var removed)) return;
        cachedRows -= removed.Rows;
        cachedBytes -= removed.Bytes;
    }
}
