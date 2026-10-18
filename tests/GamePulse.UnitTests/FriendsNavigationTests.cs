using System.Collections.Concurrent;
using GamePulse.Core;
using GamePulse.Desktop.Services;
using GamePulse.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GamePulse.UnitTests;

/// <summary>Navigation uses real production ViewModels and test-only Steam responses.</summary>
public sealed class FriendsNavigationTests
{
    private const string IdA = "76561198000000001";
    private const string IdB = "76561198000000002";
    private const string IdC = "76561198000000003";

    [Theory]
    [InlineData(FriendDestination.CompareA)]
    [InlineData(FriendDestination.CompareB)]
    public async Task SendingFriendToOneComparisonSidePreservesOtherIdentityAndFacts(FriendDestination destination)
    {
        var profiles = new Profiles();
        var libraries = new Libraries();
        using var avatars = new AvatarService();
        var comparison = new ComparisonViewModel(profiles, libraries, new ProfileComparisonService());
        using var vm = Create(profiles, avatars, comparison);
        comparison.SideA.Input = IdA;
        comparison.SideB.Input = IdB;
        await comparison.CompareAsync(false);
        var other = destination == FriendDestination.CompareA ? comparison.SideB : comparison.SideA;
        var target = destination == FriendDestination.CompareA ? comparison.SideA : comparison.SideB;
        var otherId = other.SteamId;
        var otherLibrary = other.LibraryResult;
        var otherFacts = (other.Input, other.DisplayName, other.Level, other.Games, other.Playtime, other.VacBans);
        var otherCalls = profiles.Calls.Count(call => call.Input == otherId);
        var otherLibraries = libraries.Calls.Count(call => call.Id == otherId);

        await vm.NavigateFromFriendAsync(new(IdC, destination));
        await comparison.RefreshComparisonAsync();

        Assert.True(vm.IsComparison);
        Assert.Equal(IdC, target.Input);
        Assert.Equal(IdC, target.SteamId);
        Assert.Equal(otherId, other.SteamId);
        Assert.Same(otherLibrary, other.LibraryResult);
        Assert.Equal(otherFacts, (other.Input, other.DisplayName, other.Level, other.Games, other.Playtime, other.VacBans));
        Assert.Equal(otherCalls, profiles.Calls.Count(call => call.Input == otherId));
        Assert.Equal(otherLibraries, libraries.Calls.Count(call => call.Id == otherId));
        Assert.Equal(10u, Assert.Single(comparison.MutualGames).AppId);
        Assert.Contains(profiles.Calls, call => call.Input == IdC && !call.Force);
    }

    [Fact]
    public async Task ExplorerActionFromFriendsOpensSelectedArbitraryProfileAndLeavesComparisonIntact()
    {
        var profiles = new Profiles();
        var libraries = new Libraries();
        using var avatars = new AvatarService();
        var comparison = new ComparisonViewModel(profiles, libraries, new ProfileComparisonService());
        using var vm = Create(profiles, avatars, comparison);
        vm.SearchInput = IdA;
        await vm.SearchAsync(false);
        comparison.SideB.Input = IdC;
        await comparison.SideB.SearchAsync(false);
        var comparisonLibrary = comparison.SideB.LibraryResult;
        vm.ShowFriendsCommand.Execute(null);
        Assert.True(vm.IsFriends);

        await vm.NavigateFromFriendAsync(new(IdB, FriendDestination.Explorer));

        Assert.True(vm.IsOverview);
        Assert.False(vm.IsFriends);
        Assert.Equal(IdB, vm.SearchInput);
        Assert.Equal(IdB, vm.SteamId);
        Assert.Equal(IdB, vm.RecentProfiles[0].SteamId);
        Assert.Contains(vm.RecentProfiles, profile => profile.SteamId == IdA);
        Assert.Equal(IdC, comparison.SideB.Input);
        Assert.Equal(IdC, comparison.SideB.SteamId);
        Assert.Same(comparisonLibrary, comparison.SideB.LibraryResult);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("arbitrary-vanity")]
    [InlineData("https://steamcommunity.com/profiles/76561198000000002")]
    [InlineData("https://untrusted.invalid/?key=DO_NOT_EXPOSE")]
    [InlineData("76561197960265728")]
    public async Task InvalidFriendIdsCannotTriggerLookupOrChangeCurrentPage(string steamId)
    {
        var profiles = new Profiles();
        var libraries = new Libraries();
        using var avatars = new AvatarService();
        var comparison = new ComparisonViewModel(profiles, libraries, new ProfileComparisonService());
        using var vm = Create(profiles, avatars, comparison);
        vm.SearchInput = IdA;
        comparison.SideA.Input = IdA;
        comparison.SideB.Input = IdB;
        vm.ShowFriendsCommand.Execute(null);

        await vm.NavigateFromFriendAsync(new(steamId, FriendDestination.Explorer));
        await vm.NavigateFromFriendAsync(new(steamId, FriendDestination.CompareA));
        await vm.NavigateFromFriendAsync(new(steamId, FriendDestination.CompareB));

        Assert.True(vm.IsFriends);
        Assert.Equal(IdA, vm.SearchInput);
        Assert.Equal(IdA, comparison.SideA.Input);
        Assert.Equal(IdB, comparison.SideB.Input);
        Assert.Empty(profiles.Calls);
        Assert.Empty(libraries.Calls);
        Assert.DoesNotContain("DO_NOT_EXPOSE", vm.StatusMessage);
    }

    [Fact]
    public async Task LateExplorerFriendLookupCannotOverwriteNewerSelectedProfile()
    {
        var late = Pending<SteamProfile>();
        CancellationToken oldToken = default;
        var profiles = new Profiles
        {
            Find = (input, token) =>
            {
                if (input != IdA) return Task.FromResult(Profile(input));
                oldToken = token;
                return late.Task; // Deliberately uncooperative transport.
            }
        };
        using var avatars = new AvatarService();
        using var vm = Create(profiles, avatars);
        var oldNavigation = vm.NavigateFromFriendAsync(new(IdA, FriendDestination.Explorer));

        await vm.NavigateFromFriendAsync(new(IdB, FriendDestination.Explorer));
        Assert.True(oldToken.IsCancellationRequested);
        late.SetResult(Profile(IdA));
        await oldNavigation;

        Assert.Equal(IdB, vm.SteamId);
        Assert.Equal(IdB, vm.SearchInput);
        Assert.Equal(IdB, Assert.Single(vm.RecentProfiles).SteamId);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SendingFriendToAShouldNotCancelInFlightLookupForB()
    {
        var pendingB = Pending<SteamProfile>();
        CancellationToken tokenB = default;
        var profiles = new Profiles
        {
            Find = (input, token) =>
            {
                if (input != IdB) return Task.FromResult(Profile(input));
                tokenB = token;
                return pendingB.Task;
            }
        };
        using var avatars = new AvatarService();
        var comparison = new ComparisonViewModel(profiles, new Libraries(), new ProfileComparisonService());
        using var vm = Create(profiles, avatars, comparison);
        comparison.SideB.Input = IdB;
        var searchB = comparison.SideB.SearchAsync(false);
        Assert.True(comparison.SideB.IsBusy);

        await vm.NavigateFromFriendAsync(new(IdC, FriendDestination.CompareA));

        Assert.False(tokenB.IsCancellationRequested);
        Assert.True(comparison.SideB.IsBusy);
        Assert.Equal(IdB, comparison.SideB.Input);
        Assert.Equal(IdC, comparison.SideA.SteamId);
        pendingB.SetResult(Profile(IdB));
        await searchB;
        await comparison.RefreshComparisonAsync();
        Assert.Equal(IdB, comparison.SideB.SteamId);
        Assert.Equal(10u, Assert.Single(comparison.MutualGames).AppId);
    }

    [Fact]
    public async Task DisposedWindowIgnoresFriendNavigationWithoutStartingRequests()
    {
        var profiles = new Profiles();
        using var avatars = new AvatarService();
        var comparison = new ComparisonViewModel(profiles, new Libraries(), new ProfileComparisonService());
        var vm = Create(profiles, avatars, comparison);
        vm.Dispose();

        await vm.NavigateFromFriendAsync(new(IdA, FriendDestination.Explorer));
        await vm.NavigateFromFriendAsync(new(IdB, FriendDestination.CompareB));

        Assert.Empty(profiles.Calls);
        Assert.False(vm.HasProfile);
        Assert.False(comparison.SideB.HasProfile);
    }

    [Fact]
    public async Task SelectedFriendActionEventIsWiredToExplorerNavigation()
    {
        var profiles = new Profiles();
        using var avatars = new AvatarService();
        var friends = new FriendsViewModel(new FriendsService(), avatars);
        using var vm = new MainWindowViewModel(profiles, avatars,
            NullLogger<MainWindowViewModel>.Instance, friends: friends);
        await friends.LoadAsync(IdA, false, CancellationToken.None);
        friends.SelectedFriend = Assert.Single(friends.Rows);
        vm.ShowFriendsCommand.Execute(null);
        var navigationComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.IsBusy) && !vm.IsBusy && vm.SteamId == IdB)
                navigationComplete.TrySetResult();
        };

        friends.RequestAction(FriendDestination.Explorer);
        await navigationComplete.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsOverview);
        Assert.Equal(IdB, vm.SteamId);
        Assert.Equal(IdB, vm.SearchInput);
        Assert.Single(profiles.Calls);
        Assert.Equal(IdB, profiles.Calls.Single().Input);
    }

    private static MainWindowViewModel Create(Profiles profiles, AvatarService avatars, ComparisonViewModel? comparison = null)
        => new(profiles, avatars, NullLogger<MainWindowViewModel>.Instance, comparison: comparison);

    private static TaskCompletionSource<Outcome<T>> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Outcome<SteamProfile> Profile(string id) => Outcome<SteamProfile>.Ready(new(id,
        "Navigation test " + id[^2..], "https://steamcommunity.com/profiles/" + id, null, 3, 1, null, null, null));

    private sealed class Profiles : IProfileLookupService
    {
        public ConcurrentQueue<(string Input, bool Force)> Calls { get; } = [];
        public Func<string, CancellationToken, Task<Outcome<SteamProfile>>> Find { get; init; }
            = (input, _) => Task.FromResult(Profile(input));
        public Task<Outcome<SteamProfile>> FindAsync(string input, bool forceRefresh, CancellationToken cancellationToken)
        {
            Calls.Enqueue((input, forceRefresh));
            return Find(input, cancellationToken);
        }
        public Task<Outcome<ProfileLevel>> GetLevelAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken)
            => Task.FromResult(Outcome<ProfileLevel>.Ready(new(12)));
        public Task<Outcome<SteamBans>> GetBansAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken)
            => Task.FromResult(Outcome<SteamBans>.Ready(new(false, false, 0, 0, null, "none")));
    }

    private sealed class Libraries : ILibraryService
    {
        public ConcurrentQueue<(string Id, bool Force)> Calls { get; } = [];
        public Task<Outcome<OwnedLibrary>> GetLibraryAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken)
        {
            Calls.Enqueue((steamId, forceRefresh));
            return Task.FromResult(Outcome<OwnedLibrary>.Ready(new([new(10, "Shared test game", 120, null, null)], 1, true)));
        }
        public Task<Outcome<RecentGames>> GetRecentAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FriendsService : IFriendsService
    {
        public Task<Outcome<FriendDirectory>> GetFriendsAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken)
            => Task.FromResult(Outcome<FriendDirectory>.Ready(new(
                [new(IdB, null, Profile(IdB).Data, DataState.Ready)], true, 0)));
    }
}
