using GamePulse.Core;

namespace GamePulse.Infrastructure;

/// <summary>Session-only bounded successful-result cache. Failures are never cached as missing data.</summary>
public sealed class ProfileLookupService(ISteamApiClient steam, SteamOptions options, TimeProvider clock) : IProfileLookupService
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingLoad> pending = new(StringComparer.Ordinal);
    private sealed record Entry(object Value, DateTimeOffset Expires, long Sequence);
    private sealed class PendingLoad { public long Latest; public int Count; }
    private long sequence;

    public async Task<Outcome<SteamProfile>> FindAsync(string input, bool forceRefresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = ProfileInput.Parse(input);
        if (!parsed.IsReady) return Outcome<SteamProfile>.Failure(parsed.State, parsed.Message!);
        var id = parsed.Data!.Value;
        if (parsed.Data.Kind == ProfileInputKind.Vanity)
        {
            var resolved = await CachedAsync("vanity:" + id, options.VanityTtl, forceRefresh,
                ct => steam.ResolveVanityAsync(id, ct), cancellationToken).ConfigureAwait(false);
            if (!resolved.IsReady) return Outcome<SteamProfile>.Failure(resolved.State, resolved.Message!);
            id = resolved.Data!;
        }
        return await CachedAsync("profile:" + id, options.ProfileTtl, forceRefresh,
            ct => steam.GetProfileAsync(id, ct), cancellationToken).ConfigureAwait(false);
    }

    public Task<Outcome<ProfileLevel>> GetLevelAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) =>
        CachedAsync("level:" + steamId, options.LevelTtl, forceRefresh, ct => steam.GetLevelAsync(steamId, ct), cancellationToken);

    public Task<Outcome<SteamBans>> GetBansAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) =>
        CachedAsync("bans:" + steamId, options.BansTtl, forceRefresh, ct => steam.GetBansAsync(steamId, ct), cancellationToken);

    private async Task<Outcome<T>> CachedAsync<T>(string key, TimeSpan ttl, bool force,
        Func<CancellationToken, Task<Outcome<T>>> load, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long generation;
        lock (gate)
        {
            if (force) cache.Remove(key);
            else if (cache.TryGetValue(key, out var hit))
            {
                if (hit.Expires > clock.GetUtcNow()) return ((Outcome<T>)hit.Value) with { FromCache = true };
                cache.Remove(key);
            }
            generation = ++sequence;
            if (!pending.TryGetValue(key, out var active)) pending[key] = active = new();
            active.Latest = generation;
            active.Count++;
        }
        try
        {
            var result = await load(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.IsReady && ttl > TimeSpan.Zero)
            {
                lock (gate)
                {
                    if (pending[key].Latest == generation)
                    {
                        foreach (var expired in cache.Where(x => x.Value.Expires <= clock.GetUtcNow()).Select(x => x.Key).ToArray())
                            cache.Remove(expired);
                        var capacity = Math.Clamp(options.MaxCacheEntries, 1, 4096);
                        if (cache.Count >= capacity && !cache.ContainsKey(key))
                            cache.Remove(cache.MinBy(x => x.Value.Sequence).Key);
                        cache[key] = new(result, clock.GetUtcNow() + ttl, generation);
                    }
                }
            }
            return result;
        }
        finally
        {
            lock (gate)
            {
                if (--pending[key].Count == 0) pending.Remove(key);
            }
        }
    }
}
