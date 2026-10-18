namespace GamePulse.Core;

public enum AchievementUnlockFilter { All, Unlocked, Locked }
public enum AchievementSort { Name, UnlockedFirst, LockedFirst, RecentlyUnlocked }
public sealed record AchievementFilter(string Query = "", AchievementUnlockFilter Unlock = AchievementUnlockFilter.All,
    AchievementSort Sort = AchievementSort.Name);

/// <summary>Queries the complete selected-game source. Unknown progress never becomes locked progress.</summary>
public static class AchievementQuery
{
    public static IReadOnlyList<AchievementDetail> Apply(IReadOnlyList<AchievementDetail> achievements,
        AchievementFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(achievements);
        ArgumentNullException.ThrowIfNull(filter);
        if (!Enum.IsDefined(filter.Unlock) || !Enum.IsDefined(filter.Sort))
            throw new ArgumentOutOfRangeException(nameof(filter));
        cancellationToken.ThrowIfCancellationRequested();
        var query = filter.Query?.Trim() ?? "";
        var selected = new List<AchievementDetail>(achievements.Count);
        foreach (var achievement in achievements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filter.Unlock == AchievementUnlockFilter.Unlocked && achievement.Unlocked != true) continue;
            if (filter.Unlock == AchievementUnlockFilter.Locked && achievement.Unlocked != false) continue;
            if (query.Length > 0 && !Contains(achievement.ApiName, query) &&
                !Contains(achievement.DisplayName, query) && !Contains(achievement.Description, query)) continue;
            selected.Add(achievement);
        }
        // LINQ's stable sort retains source order for exact duplicate keys. The API name breaks title ties.
        var textComparer = Comparer<string?>.Create((a, b) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StringComparer.OrdinalIgnoreCase.Compare(a, b);
        });
        IOrderedEnumerable<AchievementDetail> sorted = filter.Sort switch
        {
            AchievementSort.UnlockedFirst => selected.OrderBy(x => UnlockRank(x.Unlocked, true)),
            AchievementSort.LockedFirst => selected.OrderBy(x => UnlockRank(x.Unlocked, false)),
            AchievementSort.RecentlyUnlocked => selected.OrderBy(x => x.Unlocked != true || !x.UnlockedAt.HasValue)
                .ThenByDescending(x => x.Unlocked == true ? x.UnlockedAt : null),
            _ => selected.OrderBy(x => string.IsNullOrWhiteSpace(x.DisplayName))
        };
        AchievementDetail[] result;
        try
        {
            result = sorted.ThenBy(x => Name(x), textComparer)
                .ThenBy(x => x.ApiName, StringComparer.Ordinal).ToArray();
        }
        catch (InvalidOperationException exception) when (exception.InnerException is OperationCanceledException &&
            cancellationToken.IsCancellationRequested)
        {
            // Comparer exceptions are wrapped by the runtime; cancellation is still
            // cancellation, not a failed filter or a reason to clear selected details.
            throw new OperationCanceledException(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static bool Contains(string? value, string query) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    private static string Name(AchievementDetail achievement) => string.IsNullOrWhiteSpace(achievement.DisplayName)
        ? achievement.ApiName : achievement.DisplayName;
    private static int UnlockRank(bool? unlocked, bool first) => !unlocked.HasValue ? 2 : unlocked == first ? 0 : 1;
}

public sealed record AchievementProgressSummary(int IncludedGames, long TotalUnlocked, long TotalAvailable,
    double? WeightedCompletion, double? AveragePerGameCompletion);
public sealed record AchievementCountSnapshot(bool IsComplete, int? UnlockedCount, int? TotalCount);

/// <summary>Describes only complete positive-total games supplied by the caller, never an unvisited library.</summary>
public static class AchievementProgress
{
    public static AchievementProgressSummary Calculate(IEnumerable<GameAchievements> games,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(games);
        return CalculateCounts(games.Select(game => new AchievementCountSnapshot(game.IsComplete, game.UnlockedCount, game.TotalCount)),
            cancellationToken);
    }

    /// <summary>Aggregates compact checked-game counts without requiring full achievement payloads to stay in the UI.</summary>
    public static AchievementProgressSummary CalculateCounts(IEnumerable<AchievementCountSnapshot> games,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(games);
        cancellationToken.ThrowIfCancellationRequested();
        var included = 0;
        long unlocked = 0, available = 0;
        double percentages = 0;
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!game.IsComplete || game.TotalCount is not > 0 || game.UnlockedCount is not >= 0 ||
                game.UnlockedCount.Value > game.TotalCount.Value) continue;
            included++;
            unlocked += game.UnlockedCount.Value;
            available += game.TotalCount.Value;
            percentages += 100d * game.UnlockedCount.Value / game.TotalCount.Value;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(included, unlocked, available, included == 0 ? null : 100d * unlocked / available,
            included == 0 ? null : percentages / included);
    }
}
