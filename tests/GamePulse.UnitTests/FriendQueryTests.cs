using GamePulse.Core;
using Xunit;

namespace GamePulse.UnitTests;

public class FriendQueryTests
{
    [Fact]
    public void SortsAllSevenKnownStatesBeforeUnknownAndNeverInfersOffline()
    {
        var friends = Enumerable.Range(0, 7).Select(i => Friend(i + 1, $"State {i}", i)).Reverse()
            .Append(Friend(20, "Unknown", null)).Append(new("76561198000000021", null, null, DataState.Unavailable)).ToArray();
        var result = FriendQuery.Apply(friends, new());
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5, 6, 0, null, null }, result.Select(x => x.Profile?.PersonaState));
        Assert.Equal(9, result.Count);
    }

    [Fact]
    public void OnlineOnlyIncludesEveryKnownNonOfflineStateAndExcludesUnknown()
    {
        var friends = Enumerable.Range(0, 9).Select(i => Friend(i + 1, $"State {i}", i)).ToArray();
        Assert.Equal(6, FriendQuery.Apply(friends, new(OnlineOnly: true)).Count);
        Assert.DoesNotContain(FriendQuery.Apply(friends, new(OnlineOnly: true)), x => x.Profile!.PersonaState is 0 or > 6);
    }

    [Fact]
    public void UnavailableSummaryWithAccidentalProfileDoesNotClaimOnline()
    {
        var friend = Friend(1, "Not authoritative", 1) with { SummaryState = DataState.Failed };
        Assert.False(FriendQuery.HasSummary(friend));
        Assert.False(FriendQuery.IsOnline(friend));
        Assert.Equal(friend.SteamId, FriendQuery.Name(friend));
        Assert.Empty(FriendQuery.Apply([friend], new(OnlineOnly: true)));
    }

    [Fact]
    public void SearchesNamesCaseInsensitivelyAndMissingSummariesBySteamId()
    {
        var missing = new SteamFriend("76561198000000002", null, null, DataState.Unavailable);
        var friends = new[] { Friend(1, "Some PLAYER", 1), missing };
        Assert.Equal("Some PLAYER", Assert.Single(FriendQuery.Apply(friends, new(" player "))).Profile!.DisplayName);
        Assert.Equal(missing, Assert.Single(FriendQuery.Apply(friends, new("00000002"))));
        Assert.Empty(FriendQuery.Apply(friends, new("Not a friend")));
        Assert.Equal(2, FriendQuery.Apply(friends, new(null!)).Count);
    }

    [Fact]
    public void EqualNamesUseSteamIdForDeterministicOrdering()
    {
        var a = Friend(1, "Equal", 1);
        var b = Friend(2, "equal", 1);
        foreach (var sort in Enum.GetValues<FriendSort>())
            Assert.Equal(new[] { a.SteamId, b.SteamId }, FriendQuery.Apply([b, a], new(Sort: sort)).Select(x => x.SteamId));
    }

    [Theory]
    [InlineData(FriendSort.FriendsSinceNewest, 2, 1)]
    [InlineData(FriendSort.FriendsSinceOldest, 1, 2)]
    public void FriendshipDatesSortWithUnknownLast(FriendSort sort, int first, int second)
    {
        var older = Friend(1, "Older", 0) with { FriendsSince = DateTimeOffset.FromUnixTimeSeconds(1000) };
        var newer = Friend(2, "Newer", 0) with { FriendsSince = DateTimeOffset.FromUnixTimeSeconds(2000) };
        var unknown = Friend(3, "A unknown", 0);
        Assert.Equal(new[] { Friend(first, "", 0).SteamId, Friend(second, "", 0).SteamId, unknown.SteamId },
            FriendQuery.Apply([unknown, newer, older], new(Sort: sort)).Select(x => x.SteamId));
    }

    [Fact]
    public void LargeDirectoryRetainsEveryFriendAndDoesNotMutateSource()
    {
        var friends = Enumerable.Range(1, 12_000).Select(i => Friend(i, $"Player {i:D5}", i % 7)).ToArray();
        var result = FriendQuery.Apply(friends, new(Sort: FriendSort.Name));
        Assert.Equal(12_000, result.Count);
        Assert.Equal(friends[^1].SteamId, result[^1].SteamId);
        Assert.Equal("Player 00001", friends[0].Profile!.DisplayName);
    }

    [Fact]
    public void EmptyAndCancelledQueriesHaveExplicitBehavior()
    {
        Assert.Empty(FriendQuery.Apply([], new()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => FriendQuery.Apply([], new(), cancelled.Token));
    }

    internal static SteamFriend Friend(int id, string name, int? state, string? avatar = null) =>
        new((76561198000000000UL + (uint)id).ToString(System.Globalization.CultureInfo.InvariantCulture), null,
            new((76561198000000000UL + (uint)id).ToString(System.Globalization.CultureInfo.InvariantCulture), name, "https://steamcommunity.com/", avatar,
                3, state, null, null, null), DataState.Ready);
}
