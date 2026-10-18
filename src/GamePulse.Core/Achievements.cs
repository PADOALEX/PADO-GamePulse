namespace GamePulse.Core;

public sealed record AchievementDefinition(string ApiName, string? DisplayName, string? Description, bool? Hidden);
public sealed record PlayerAchievement(string ApiName, bool Unlocked, DateTimeOffset? UnlockedAt);
public sealed record AchievementSchema(uint AppId, string? GameName, IReadOnlyList<AchievementDefinition> Achievements);
public sealed record PlayerAchievements(uint AppId, IReadOnlyList<PlayerAchievement> Achievements);
public sealed record AchievementDetail(string ApiName, string? DisplayName, string? Description,
    bool? Hidden, bool? Unlocked, DateTimeOffset? UnlockedAt);

/// <summary>Incomplete/mismatched sources never establish a numeric completion rate.</summary>
public sealed record GameAchievements(uint AppId, string? GameName, IReadOnlyList<AchievementDetail> Achievements, bool IsComplete)
{
    public int? TotalCount => IsComplete && Achievements.All(x => x.Unlocked.HasValue) ? Achievements.Count : null;
    public int? UnlockedCount => TotalCount.HasValue ? Achievements.Count(x => x.Unlocked == true) : null;
    public double? Completion => TotalCount is > 0 ? 100d * UnlockedCount!.Value / TotalCount.Value : null;
}

public interface IAchievementService
{
    Task<Outcome<GameAchievements>> GetAchievementsAsync(string steamId, uint appId, bool forceRefresh,
        CancellationToken cancellationToken);
}
