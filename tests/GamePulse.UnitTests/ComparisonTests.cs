using GamePulse.Core;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class ComparisonTests
{
    private readonly ProfileComparisonService _service = new();

    [Fact]
    public void ArbitraryAvailableLibrariesIntersectByAppIdAndPreserveEachPlaytime()
    {
        var result = _service.Compare(Library(Game(10, "Same game", 60), Game(20, "Only A", 180)),
            Library(Game(10, "Renamed", 120), Game(30, "Only B", 240)));
        Assert.Equal(DataState.Ready, result.State);
        var data = Assert.IsType<ProfileComparison>(result.Data);
        var mutual = Assert.Single(data.MutualGames);
        Assert.Equal(10u, mutual.AppId);
        Assert.Equal("Same game", mutual.Name);
        Assert.Equal(60L, mutual.MinutesA);
        Assert.Equal(120L, mutual.MinutesB);
        Assert.Equal(2, data.GamesA);
        Assert.Equal(2, data.GamesB);
        Assert.Equal(240L, data.TotalMinutesA);
        Assert.Equal(360L, data.TotalMinutesB);
    }

    [Theory]
    [InlineData(DataState.Private, DataState.Private)]
    [InlineData(DataState.Loading, DataState.Loading)]
    [InlineData(DataState.Unavailable, DataState.Unavailable)]
    [InlineData(DataState.Failed, DataState.Unavailable)]
    [InlineData(DataState.MissingKey, DataState.Unavailable)]
    [InlineData(DataState.RateLimited, DataState.Unavailable)]
    [InlineData(DataState.NotFound, DataState.Unavailable)]
    public void UnavailableLibraryNeverBecomesZeroMutualGames(DataState source, DataState expected)
    {
        var result = _service.Compare(Library(Game(10, "Game", 60)), new(source));
        Assert.Equal(expected, result.State);
        Assert.Null(result.Data);
    }

    [Fact]
    public void NoLoadedLibraryIsUnknown()
    {
        var result = _service.Compare(null, null);
        Assert.Equal(DataState.Unavailable, result.State);
        Assert.Null(result.Data);
    }

    [Fact]
    public void PrivateTakesPrecedenceOverStillLoadingToExplainPrivacy()
    {
        var result = _service.Compare(new(DataState.Private), new(DataState.Loading));
        Assert.Equal(DataState.Private, result.State);
        Assert.Null(result.Data);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IncompleteLibraryDoesNotClaimACompleteIntersection(bool incompleteA)
    {
        var incomplete = Outcome<OwnedLibrary>.Ready(new([Game(10, "Game", 20)], 100, false));
        var complete = Library(Game(10, "Game", 20));
        var result = _service.Compare(incompleteA ? incomplete : complete, incompleteA ? complete : incomplete);
        Assert.Equal(DataState.Unavailable, result.State);
        Assert.Null(result.Data);
    }

    [Fact]
    public void CompleteEmptyLibraryIsKnownZero()
    {
        var result = _service.Compare(Library(), Library(Game(20, "Other", 60)));
        Assert.Equal(DataState.Empty, result.State);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.MutualGames);
        Assert.Equal(0, result.Data.GamesA);
        Assert.Equal(0L, result.Data.TotalMinutesA);
        Assert.Equal(60L, result.Data.TotalMinutesB);
    }

    [Fact]
    public void RealEmptyIntersectionIsDifferentFromUnavailable()
    {
        var result = _service.Compare(Library(Game(10, "A", 60)), Library(Game(20, "B", 120)));
        Assert.Equal(DataState.Empty, result.State);
        Assert.Empty(result.Data!.MutualGames);
        Assert.Equal(1, result.Data.GamesA);
        Assert.Equal(1, result.Data.GamesB);
    }

    [Fact]
    public void MissingPlaytimeStaysUnknownWhileZeroRemainsKnown()
    {
        var result = _service.Compare(Library(Game(10, "Game", null)), Library(Game(10, "Game", 0)));
        Assert.Null(result.Data!.TotalMinutesA);
        Assert.Equal(0L, result.Data.TotalMinutesB);
        Assert.Null(Assert.Single(result.Data.MutualGames).MinutesA);
        Assert.Equal(0L, result.Data.MutualGames[0].MinutesB);
    }

    [Fact]
    public void MissingNameCanUseOtherLibraryButNeverInventMetadata()
    {
        var result = _service.Compare(Library(Game(10, null, 0)), Library(Game(10, "Available name", 60)));
        Assert.Equal("Available name", Assert.Single(result.Data!.MutualGames).Name);
    }

    [Fact]
    public void DuplicateAppIdsDoNotInflateCountsTotalsOrMutualRows()
    {
        var result = _service.Compare(Library(Game(10, "A", 60), Game(10, "A", 60)),
            Library(Game(10, "A", 120), Game(10, "A", 120)));
        Assert.Single(result.Data!.MutualGames);
        Assert.Equal(1, result.Data.GamesA);
        Assert.Equal(1, result.Data.GamesB);
        Assert.Equal(60L, result.Data.TotalMinutesA);
        Assert.Equal(120L, result.Data.TotalMinutesB);
    }

    [Fact]
    public void OverflowCannotWrapTotalIntoPlausibleNumber()
    {
        var result = _service.Compare(Library(Game(10, "A", long.MaxValue), Game(20, "B", 1)), Library());
        Assert.Null(result.Data!.TotalMinutesA);
    }

    [Fact]
    public void LargeLibrariesIncludeEveryMutualGameWithoutDisplayCap()
    {
        var a = Enumerable.Range(1, 10_000).Select(id => Game((uint)id, "Game " + id, id)).ToArray();
        var b = Enumerable.Range(5_001, 10_000).Select(id => Game((uint)id, "Game " + id, id * 2L)).ToArray();
        var result = _service.Compare(Library(a), Library(b));
        Assert.Equal(5_000, result.Data!.MutualGames.Count);
        Assert.Equal(10_000, result.Data.GamesA);
        Assert.Equal(10_000, result.Data.GamesB);
        Assert.Contains(result.Data.MutualGames, row => row.AppId == 10_000 && row.MinutesB == 20_000);
    }

    [Fact]
    public void ComparisonFreshnessReflectsOlderLibrarySnapshot()
    {
        var older = DateTimeOffset.UtcNow.AddMinutes(-10);
        var newer = older.AddMinutes(5);
        var a = Library(Game(10, "Game", 60)) with { UpdatedAt = older, FromCache = true };
        var b = Library(Game(10, "Game", 120)) with { UpdatedAt = newer };
        var result = _service.Compare(a, b);
        Assert.Equal(older, result.UpdatedAt);
        Assert.True(result.FromCache);
    }

    private static LibraryGame Game(uint id, string? name, long? minutes) => new(id, name, minutes, null, null);
    private static Outcome<OwnedLibrary> Library(params LibraryGame[] games) =>
        new(games.Length == 0 ? DataState.Empty : DataState.Ready, new(games, games.Length, true));
}
