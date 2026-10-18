using GamePulse.Core;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class LibraryInsightsTests
{
    [Fact]
    public void Complete_library_has_exact_totals_and_weighted_concentration()
    {
        LibraryGame[] games = [Game(1, 600), Game(2, 300), Game(3, 60), Game(4, 40), Game(5, 0)];
        var result = LibraryInsights.Calculate(new(games, games.Length, true));
        Assert.True(result.IsComplete);
        Assert.Equal(1000m, result.TotalMinutes);
        Assert.Equal(1, result.UnplayedCount);
        Assert.Equal(20, result.UnplayedShare);
        Assert.Equal(96, result.TopThreePlaytimeShare);
        Assert.Equal(5, result.Buckets.Sum(bucket => bucket.Count));
        Assert.Equal(100, result.Buckets.Sum(bucket => bucket.ShareOfAvailableGames), 8);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, result.TopGames.Select(game => game.AppId));
    }

    [Fact]
    public void Bucket_boundaries_do_not_overlap_and_unknown_never_means_unplayed()
    {
        long?[] minutes = [0, 1, 59, 60, 599, 600, 2999, 3000, 5999, 6000, null, -1];
        var games = minutes.Select((value, index) => Game((uint)index + 1, value)).ToArray();
        var result = LibraryInsights.Calculate(new(games, games.Length, true));
        Assert.Equal(new[] { 1, 2, 2, 2, 2, 1, 2 }, result.Buckets.Select(bucket => bucket.Count));
        Assert.Equal(10, result.KnownPlaytimeCount);
        Assert.Null(result.TotalMinutes);
        Assert.Null(result.UnplayedCount);
        Assert.Null(result.UnplayedShare);
        Assert.Null(result.TopThreePlaytimeShare);
        Assert.DoesNotContain(result.TopGames, game => game.PlaytimeMinutes is null or < 0);
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    [InlineData(false, 2)]
    public void Partial_or_inconsistent_library_has_only_returned_game_facts(bool complete, int reported)
    {
        var result = LibraryInsights.Calculate(new([Game(1, 0), Game(2, 120)], reported, complete));
        Assert.False(result.IsComplete);
        Assert.Equal(120m, result.KnownMinutes);
        Assert.Null(result.TotalMinutes);
        Assert.Null(result.UnplayedCount);
        Assert.Null(result.UnplayedShare);
        Assert.Null(result.TopThreePlaytimeShare);
        Assert.Equal(1, result.Buckets.Single(bucket => bucket.Range == PlaytimeRange.Unplayed).Count);
    }

    [Fact]
    public void Empty_library_has_zero_counts_and_unknown_percentages()
    {
        var result = LibraryInsights.Calculate(new([], 0, true));
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.TotalMinutes);
        Assert.Equal(0, result.UnplayedCount);
        Assert.Null(result.UnplayedShare);
        Assert.Null(result.TopThreePlaytimeShare);
        Assert.All(result.Buckets, bucket => { Assert.Equal(0, bucket.Count); Assert.Equal(0, bucket.ShareOfAvailableGames); });
        Assert.Empty(result.TopGames);
    }

    [Fact]
    public void All_unplayed_is_full_coverage_without_a_playtime_denominator()
    {
        var result = LibraryInsights.Calculate(new([Game(1, 0), Game(2, 0)], 2, true));
        Assert.Equal(0, result.TotalMinutes);
        Assert.Equal(2, result.UnplayedCount);
        Assert.Equal(100, result.UnplayedShare);
        Assert.Null(result.TopThreePlaytimeShare);
    }

    [Fact]
    public void Totals_can_exceed_int64_without_overflow()
    {
        var result = LibraryInsights.Calculate(new([Game(1, long.MaxValue), Game(2, long.MaxValue)], 2, true));
        Assert.Equal((decimal)long.MaxValue * 2, result.TotalMinutes);
        Assert.Equal(100, result.TopThreePlaytimeShare);
    }

    [Fact]
    public void Large_library_aggregates_every_row_and_explicitly_returns_only_top_ten()
    {
        var games = Enumerable.Range(1, 20000).Select(index => Game((uint)index, index)).ToArray();
        var result = LibraryInsights.Calculate(new(games, games.Length, true));
        Assert.Equal(20000, result.AvailableCount);
        Assert.Equal(20000, result.KnownPlaytimeCount);
        Assert.Equal(20000, result.Buckets.Sum(bucket => bucket.Count));
        Assert.Equal(200010000m, result.TotalMinutes);
        Assert.Equal(10, result.TopGames.Count);
        Assert.Equal(20000u, result.TopGames[0].AppId);
        Assert.Equal(19991u, result.TopGames[^1].AppId);
    }

    [Fact]
    public void Cancellation_does_not_return_incomplete_aggregates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => LibraryInsights.Calculate(new([Game(1, 60)], 1, true), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => LibraryInsights.Calculate(new([], 0, true), cancellation.Token));
    }

    private static LibraryGame Game(uint id, long? minutes) => new(id, $"Game {id}", minutes, null, null);
}
