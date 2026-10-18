namespace GamePulse.Infrastructure;

/// <summary>Local settings only. The API key is never read from legacy files or persisted.</summary>
public sealed class SteamOptions
{
    public string? ApiKey { get; init; }
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxConcurrentRequests { get; init; } = 4;
    public int MaxAttempts { get; init; } = 3;
    public int MaxCacheEntries { get; init; } = 256;
    public TimeSpan ProfileTtl { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan LevelTtl { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan BansTtl { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan VanityTtl { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan LibraryTtl { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan RecentTtl { get; init; } = TimeSpan.FromMinutes(2);
    public int MaxLibraryCacheRows { get; init; } = 100_000;
    public long MaxLibraryCacheBytes { get; init; } = 32 * 1024 * 1024;
    public TimeSpan FriendsTtl { get; init; } = TimeSpan.FromMinutes(3);
    public int MaxFriendsCacheRows { get; init; } = 30_000;
    public long MaxFriendsCacheBytes { get; init; } = 16 * 1024 * 1024;
    // Local scheduling policy, not a claimed Steam rate limit. Shared HTTP gating still applies.
    public int MaxConcurrentFriendBatches { get; init; } = 2;
    public TimeSpan AchievementSchemaTtl { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan PlayerAchievementsTtl { get; init; } = TimeSpan.FromMinutes(3);
    // Separate schema/player caches each use these local budgets; no persistence or hidden row truncation.
    public int MaxAchievementCacheRows { get; init; } = 30_000;
    public long MaxAchievementCacheBytes { get; init; } = 16 * 1024 * 1024;
    public TimeSpan InventoryTtl { get; init; } = TimeSpan.FromMinutes(2);
    public int MaxInventoryCacheRows { get; init; } = 30_000;
    public long MaxInventoryCacheBytes { get; init; } = 16 * 1024 * 1024;
    // Local resource policies, not Steam quotas. A reached safety bound produces an explicit partial/failure state.
    public int MaxConcurrentInventoryRequests { get; init; } = 1;
    public int InventoryPageSize { get; init; } = 2000;
    public int MaxInventoryAssets { get; init; } = 250_000;
    public long MaxInventoryResponseBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxInventoryPages { get; init; } = 1000;

    public static SteamOptions FromEnvironment() => new()
    {
        ApiKey = Environment.GetEnvironmentVariable("GAMEPULSE_STEAM_API_KEY")
    };

    // Deliberately omit all setting values so accidental interpolation cannot expose secrets.
    public override string ToString() => nameof(SteamOptions);
}

public sealed class SteamRequestMetrics
{
    private long requests;
    private long retries;
    public long Requests => Interlocked.Read(ref requests);
    public long Retries => Interlocked.Read(ref retries);
    internal void RecordRequest() => Interlocked.Increment(ref requests);
    internal void RecordRetry() => Interlocked.Increment(ref retries);
}

public interface IRetryDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class RetryDelay(TimeProvider timeProvider) : IRetryDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, timeProvider, cancellationToken);
}
