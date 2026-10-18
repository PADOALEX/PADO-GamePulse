using GamePulse.Core;
using Xunit;

namespace GamePulse.UnitTests;

public class LibraryQueryTests
{
    private static readonly LibraryGame[] Games =
    [
        new(730, "Counter-Strike 2", 180, 20, DateTimeOffset.UnixEpoch.AddDays(40)),
        new(440, "Team Fortress 2", 60, null, DateTimeOffset.UnixEpoch.AddDays(60)),
        new(570, "Dota 2", 0, 0, null),
        new(999, null, null, null, null)
    ];

    [Theory]
    [InlineData(" COUNTER ", 730u)]
    [InlineData("fortress", 440u)]
    [InlineData("999", 999u)]
    public void QueryMatchesNameOrAppIdWithoutCaseSensitivity(string query, uint id)
    {
        var result = LibraryQuery.Apply(Games, new(Query: query));
        Assert.Equal(id, Assert.Single(result).AppId);
    }

    [Fact]
    public void NumericBoundsAreInclusiveAndDoNotTurnUnknownMinutesIntoZero()
    {
        var result = LibraryQuery.Apply(Games, new(MinimumHours: 0, MaximumHours: 1));
        Assert.Equal(new uint[] { 440, 570 }, result.Select(x => x.AppId));
    }

    [Fact]
    public void NeverPlayedExcludesUnknownAndNegativeValues()
    {
        var result = LibraryQuery.Apply([.. Games, new(123, "Bad data", -1, null, null)], new(UnplayedOnly: true));
        Assert.Equal(570u, Assert.Single(result).AppId);
    }

    [Theory]
    [InlineData(LibrarySort.MostPlayed, 730u)]
    [InlineData(LibrarySort.LeastPlayed, 570u)]
    [InlineData(LibrarySort.Name, 730u)]
    [InlineData(LibrarySort.RecentlyPlayed, 440u)]
    public void SortingPutsUnknownValuesLast(LibrarySort sort, uint first)
    {
        var result = LibraryQuery.Apply(Games, new(Sort: sort));
        Assert.Equal(first, result[0].AppId);
        if (sort == LibrarySort.RecentlyPlayed)
        {
            Assert.All(result.Take(2), game => Assert.NotNull(game.LastPlayed));
            Assert.All(result.Skip(2), game => Assert.Null(game.LastPlayed));
        }
        else Assert.Equal(999u, result[^1].AppId);
    }

    [Fact]
    public void TiesHaveADeterministicAppIdOrder()
    {
        LibraryGame[] same = [new(2, "Title", 30, null, null), new(1, "Title", 30, null, null)];
        Assert.Equal(new uint[] { 1, 2 }, LibraryQuery.Apply(same, new()).Select(x => x.AppId));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidNumericBoundsAreRejected(double hours)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LibraryQuery.Apply(Games, new(MinimumHours: hours)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LibraryQuery.Apply(Games, new(MaximumHours: hours)));
    }

    [Fact]
    public void ReversedBoundsAreRejected() => Assert.Throws<ArgumentException>(() => LibraryQuery.Apply(Games, new(MinimumHours: 5, MaximumHours: 2)));

    [Fact]
    public void CancelledWorkDoesNotReturnPartialResults()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => LibraryQuery.Apply(Games, new(), cts.Token));
    }

    [Fact]
    public void LargeLibraryIsQueriedWithoutTruncationOrSourceMutation()
    {
        var source = Enumerable.Range(1, 20_000).Select(i => new LibraryGame((uint)i, $"Game {i:D5}", i % 600, null, null)).ToArray();
        var result = LibraryQuery.Apply(source, new(MinimumHours: 1, MaximumHours: 4, Sort: LibrarySort.Name));
        Assert.Equal(source.Count(x => x.PlaytimeMinutes is >= 60 and <= 240), result.Count);
        Assert.True(result.Count > 400);
        Assert.Equal(1u, source[0].AppId);
        Assert.All(result, x => Assert.InRange(x.PlaytimeMinutes!.Value, 60L, 240L));
    }

    [Fact]
    public void NoMatchesIsAnEmptyList() => Assert.Empty(LibraryQuery.Apply(Games, new(Query: "not a title")));
}
