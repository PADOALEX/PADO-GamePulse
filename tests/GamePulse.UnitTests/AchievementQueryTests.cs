using GamePulse.Core;
using Xunit;

namespace GamePulse.UnitTests;

public class AchievementQueryTests
{
    [Theory]
    [InlineData(" champion ")]
    [InlineData("GOLD medal")]
    [InlineData("ACH_WIN")]
    public void SearchMatchesNameDescriptionAndApiNameWithoutCaseSensitivity(string query)
    {
        AchievementDetail[] achievements = [Detail("ach_win", "Champion", true, description: "Earn a gold medal"), Detail("other", "Other", false)];
        Assert.Equal("ach_win", Assert.Single(AchievementQuery.Apply(achievements, new(query))).ApiName);
    }

    [Theory]
    [InlineData(AchievementUnlockFilter.All, 3)]
    [InlineData(AchievementUnlockFilter.Unlocked, 1)]
    [InlineData(AchievementUnlockFilter.Locked, 1)]
    public void UnknownStateIsRetainedOnlyInAllFilter(AchievementUnlockFilter unlock, int count)
    {
        AchievementDetail[] achievements = [Detail("unlocked", "A", true), Detail("locked", "B", false), Detail("unknown", "C", null)];
        var result = AchievementQuery.Apply(achievements, new(Unlock: unlock));
        Assert.Equal(count, result.Count);
        if (unlock != AchievementUnlockFilter.All) Assert.DoesNotContain(result, x => x.Unlocked is null);
    }

    [Theory]
    [InlineData(AchievementSort.UnlockedFirst, "unlocked", "locked")]
    [InlineData(AchievementSort.LockedFirst, "locked", "unlocked")]
    public void StatusSortsUnknownLast(AchievementSort sort, string first, string second)
    {
        AchievementDetail[] achievements = [Detail("unknown", "A", null), Detail("locked", "B", false), Detail("unlocked", "C", true)];
        Assert.Equal(new[] { first, second, "unknown" }, AchievementQuery.Apply(achievements, new(Sort: sort)).Select(x => x.ApiName));
    }

    [Fact]
    public void RecentSortUsesKnownUnlockDatesAndNeverAnAccidentalLockedTimestamp()
    {
        var old = Detail("old", "Older", true, DateTimeOffset.UnixEpoch.AddDays(1));
        var recent = Detail("recent", "Recent", true, DateTimeOffset.UnixEpoch.AddDays(2));
        var locked = Detail("locked", "Locked", false, DateTimeOffset.UnixEpoch.AddYears(2));
        var unknown = Detail("unknown", "Unknown", null, DateTimeOffset.UnixEpoch.AddYears(3));
        var missing = Detail("missing", "Missing date", true);
        var result = AchievementQuery.Apply([unknown, missing, old, recent, locked], new(Sort: AchievementSort.RecentlyUnlocked));
        Assert.Equal(new[] { "recent", "old" }, result.Take(2).Select(x => x.ApiName));
        Assert.Equal(5, result.Count);
        Assert.All(result.Skip(2), x => Assert.True(x.Unlocked != true || x.UnlockedAt is null));
    }

    [Fact]
    public void TitleTiesUseApiNamesAndExactDuplicatesRetainSourceOrder()
    {
        var first = Detail("b", "Same", true, description: "first source occurrence");
        var second = Detail("b", "Same", true, description: "second source occurrence");
        var a = Detail("a", "same", true);
        foreach (var sort in Enum.GetValues<AchievementSort>())
            Assert.Equal(new[] { a, first, second }, AchievementQuery.Apply([first, second, a], new(Sort: sort)));
    }

    [Fact]
    public void AbsentDisplayNameFallsBackToApiNameWithoutLosingRows()
    {
        AchievementDetail[] achievements = [Detail("fallback", null, false), Detail("named", "Z title", true)];
        Assert.Equal("fallback", Assert.Single(AchievementQuery.Apply(achievements, new("FALLBACK"))).ApiName);
        Assert.Equal(new[] { "named", "fallback" }, AchievementQuery.Apply(achievements, new()).Select(x => x.ApiName));
        Assert.Equal(2, AchievementQuery.Apply(achievements, new(null!)).Count);
    }

    [Fact]
    public void CompleteLargeSourceHasNoHiddenLimitAndIsNotMutated()
    {
        var source = Enumerable.Range(1, 12_000).Select(i => Detail($"ach_{i:D5}", $"Achievement {i:D5}", i % 2 == 0)).Reverse().ToArray();
        var result = AchievementQuery.Apply(source, new());
        Assert.Equal(12_000, result.Count);
        Assert.Equal("ach_12000", result[^1].ApiName);
        Assert.Equal("ach_12000", source[0].ApiName);
        Assert.Equal(6_000, AchievementQuery.Apply(source, new(Unlock: AchievementUnlockFilter.Locked)).Count);
    }

    [Fact]
    public void EmptyNoMatchesAndCancellationHaveExplicitBehavior()
    {
        Assert.Empty(AchievementQuery.Apply([], new()));
        Assert.Empty(AchievementQuery.Apply([Detail("id", "Name", false)], new("Absent")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => AchievementQuery.Apply([], new(), cancelled.Token));
    }

    [Fact]
    public void InvalidFiltersAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => AchievementQuery.Apply(null!, new()));
        Assert.Throws<ArgumentNullException>(() => AchievementQuery.Apply([], null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => AchievementQuery.Apply([], new(Unlock: (AchievementUnlockFilter)99)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AchievementQuery.Apply([], new(Sort: (AchievementSort)99)));
    }

    [Theory]
    [InlineData(0, 4, 0d)]
    [InlineData(4, 4, 100d)]
    [InlineData(1, 4, 25d)]
    public void CompleteGameProgressPreservesZeroAndHundredPercent(int unlocked, int total, double percent)
    {
        var game = Game(1, unlocked, total);
        Assert.Equal(total, game.TotalCount);
        Assert.Equal(unlocked, game.UnlockedCount);
        Assert.Equal(percent, game.Completion);
        var summary = AchievementProgress.Calculate([game]);
        Assert.Equal(1, summary.IncludedGames);
        Assert.Equal(unlocked, summary.TotalUnlocked);
        Assert.Equal(total, summary.TotalAvailable);
        Assert.Equal(percent, summary.WeightedCompletion);
        Assert.Equal(percent, summary.AveragePerGameCompletion);
    }

    [Fact]
    public void WeightedAndPerGameAverageHaveSeparateDenominators()
    {
        var summary = AchievementProgress.Calculate([Game(1, 1, 1), Game(2, 0, 9)]);
        Assert.Equal(2, summary.IncludedGames);
        Assert.Equal(1, summary.TotalUnlocked);
        Assert.Equal(10, summary.TotalAvailable);
        Assert.Equal(10d, summary.WeightedCompletion);
        Assert.Equal(50d, summary.AveragePerGameCompletion);
    }

    [Fact]
    public void PartialUnknownAndZeroTotalGamesDoNotFabricateProgress()
    {
        var partial = Game(1, 1, 1) with { IsComplete = false };
        var unknown = new GameAchievements(2, "Unknown", [Detail("unknown", "Unknown", null)], true);
        var empty = Game(3, 0, 0);
        Assert.Null(partial.TotalCount);
        Assert.Null(unknown.TotalCount);
        Assert.Null(empty.Completion);
        var summary = AchievementProgress.Calculate([partial, unknown, empty]);
        Assert.Equal(0, summary.IncludedGames);
        Assert.Equal(0, summary.TotalUnlocked);
        Assert.Equal(0, summary.TotalAvailable);
        Assert.Null(summary.WeightedCompletion);
        Assert.Null(summary.AveragePerGameCompletion);
    }

    [Fact]
    public void CompleteGamesAreAggregatedIndependentlyOfExcludedSources()
    {
        var partial = Game(2, 8, 10) with { IsComplete = false };
        var summary = AchievementProgress.Calculate([partial, Game(1, 2, 4), Game(3, 0, 0)]);
        Assert.Equal(new AchievementProgressSummary(1, 2, 4, 50, 50), summary);
    }

    [Fact]
    public void CompactCountsHaveTheSameMetricsWithoutRetainingFullPayloads()
    {
        var result = AchievementProgress.CalculateCounts([new(true, 1, 1), new(true, 0, 9)]);
        Assert.Equal(new AchievementProgressSummary(2, 1, 10, 10, 50), result);
    }

    [Theory]
    [InlineData(false, 1, 2)]
    [InlineData(true, null, 2)]
    [InlineData(true, 1, null)]
    [InlineData(true, 0, 0)]
    [InlineData(true, -1, 2)]
    [InlineData(true, 3, 2)]
    public void CompactCountsRejectIncompleteUnknownZeroAndInconsistentFacts(bool complete, int? unlocked, int? total)
    {
        var result = AchievementProgress.CalculateCounts([new(complete, unlocked, total)]);
        Assert.Equal(new AchievementProgressSummary(0, 0, 0, null, null), result);
    }

    [Fact]
    public void AggregateIsCancellableDuringEnumerationAndDoesNotReturnPartialSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<GameAchievements> Games()
        {
            yield return Game(1, 1, 2);
            cancellation.Cancel();
            yield return Game(2, 1, 2);
        }
        Assert.Throws<OperationCanceledException>(() => AchievementProgress.Calculate(Games(), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => AchievementProgress.Calculate([], cancellation.Token));
        Assert.Throws<ArgumentNullException>(() => AchievementProgress.Calculate(null!));
    }

    private static AchievementDetail Detail(string id, string? name, bool? unlocked, DateTimeOffset? date = null, string? description = null) =>
        new(id, name, description, false, unlocked, date);

    private static GameAchievements Game(uint appId, int unlocked, int total) => new(appId, $"Game {appId}",
        Enumerable.Range(0, total).Select(i => Detail($"ach_{i}", $"Achievement {i}", i < unlocked)).ToArray(), true);
}
