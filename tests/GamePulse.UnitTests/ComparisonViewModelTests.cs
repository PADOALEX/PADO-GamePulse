using System.Collections.Concurrent;
using GamePulse.Core;
using GamePulse.Desktop.ViewModels;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class ComparisonViewModelTests
{
    private const string IdA = "76561198000000001";
    private const string IdB = "76561198000000002";
    private const string IdC = "76561198000000003";

    [Fact]
    public async Task BothInputsAreIndependentArbitraryProfilesIncludingVanityUrls()
    {
        var profiles = new Profiles
        {
            Find = (input, _) => Task.FromResult(Profile(input == "https://steamcommunity.com/id/arbitrary-a/" ? IdA : IdB))
        };
        using var vm = Create(profiles, new Libraries());
        vm.SideA.Input = "https://steamcommunity.com/id/arbitrary-a/";
        vm.SideB.Input = "another-vanity";
        await vm.CompareAsync(false);
        Assert.Equal(IdA, vm.SideA.SteamId);
        Assert.Equal(IdB, vm.SideB.SteamId);
        Assert.Contains(profiles.Inputs, call => call.Input == vm.SideA.Input && !call.Force);
        Assert.Contains(profiles.Inputs, call => call.Input == vm.SideB.Input && !call.Force);
        Assert.Equal(DataState.Ready, vm.State);
        Assert.Single(vm.MutualGames);
        Assert.Equal("Profile A", vm.SideA.Label);
        Assert.Equal("Profile B", vm.SideB.Label);
    }

    [Fact]
    public async Task IdentityIsVisibleBeforeIndependentLevelBansAndLibraryComplete()
    {
        var level = Pending<ProfileLevel>();
        var bans = Pending<SteamBans>();
        var library = Pending<OwnedLibrary>();
        var profiles = new Profiles { Level = (_, _) => level.Task, Bans = (_, _) => bans.Task };
        using var vm = Create(profiles, new Libraries { Library = (_, _) => library.Task });
        vm.SideA.Input = IdA;
        var search = vm.SideA.SearchAsync(false);
        Assert.True(vm.SideA.HasProfile);
        Assert.True(vm.SideA.IsBusy);
        Assert.False(search.IsCompleted);
        Assert.Equal("N/A", vm.SideA.Level);
        Assert.Equal("N/A", vm.SideA.Games);
        Assert.Equal(DataState.Loading, vm.SideA.LibraryResult!.State);
        level.SetResult(Outcome<ProfileLevel>.Failure(DataState.Unavailable, "Not exposed"));
        bans.SetResult(Outcome<SteamBans>.Ready(new(null, null, null, null, null, null)));
        library.SetResult(Library(Game(10, "Game", null)));
        await search;
        Assert.False(vm.SideA.IsBusy);
        Assert.Equal("N/A", vm.SideA.Level);
        Assert.Equal("N/A", vm.SideA.VacBans);
        Assert.Equal("N/A", vm.SideA.VacStatus);
        Assert.Equal("N/A", vm.SideA.CommunityBan);
        Assert.Equal("N/A", vm.SideA.Playtime);
        Assert.Equal("1", vm.SideA.Games);
    }

    [Fact]
    public async Task ReplacingProfileARejectsLateIdentityWhilePreservingProfileB()
    {
        var late = Pending<SteamProfile>();
        CancellationToken oldToken = default;
        var profiles = new Profiles
        {
            Find = (input, token) =>
            {
                if (input != IdA) return Task.FromResult(Profile(input));
                oldToken = token;
                return late.Task; // Ignore cancellation deliberately.
            }
        };
        using var vm = Create(profiles, new Libraries());
        vm.SideB.Input = IdB;
        await vm.SideB.SearchAsync(false);
        vm.SideA.Input = IdA;
        var old = vm.SideA.SearchAsync(false);
        vm.SideA.Input = IdC;
        await vm.SideA.SearchAsync(false);
        Assert.True(oldToken.IsCancellationRequested);
        late.SetResult(Profile(IdA));
        await old;
        await vm.RefreshComparisonAsync();
        Assert.Equal(IdC, vm.SideA.SteamId);
        Assert.Equal(IdB, vm.SideB.SteamId);
        Assert.Single(vm.MutualGames);
    }

    [Fact]
    public async Task ReplacingOneSideRejectsEveryOldEnrichmentModule()
    {
        var oldLevel = Pending<ProfileLevel>();
        var oldBans = Pending<SteamBans>();
        var oldLibrary = Pending<OwnedLibrary>();
        var profiles = new Profiles
        {
            Level = (id, _) => id == IdA ? oldLevel.Task : Task.FromResult(Outcome<ProfileLevel>.Ready(new(42))),
            Bans = (id, _) => id == IdA ? oldBans.Task : Task.FromResult(Outcome<SteamBans>.Ready(new(false, false, 0, 0, null, "none")))
        };
        var libraries = new Libraries { Library = (id, _) => id == IdA ? oldLibrary.Task : Task.FromResult(Library(Game(10, "Current", 120))) };
        using var vm = Create(profiles, libraries);
        vm.SideB.Input = IdB;
        await vm.SideB.SearchAsync(false);
        vm.SideA.Input = IdA;
        var old = vm.SideA.SearchAsync(false);
        vm.SideA.Input = IdC;
        await vm.SideA.SearchAsync(false);
        oldLevel.SetResult(Outcome<ProfileLevel>.Ready(new(999)));
        oldBans.SetResult(Outcome<SteamBans>.Ready(new(true, true, 99, 99, 1, "banned")));
        oldLibrary.SetResult(Library(Game(999, "Old", 999)));
        await old;
        await vm.RefreshComparisonAsync();
        Assert.Equal(IdC, vm.SideA.SteamId);
        Assert.Equal(IdB, vm.SideB.SteamId);
        Assert.Equal("42", vm.SideA.Level);
        Assert.Equal("0", vm.SideA.VacBans);
        Assert.Equal("No", vm.SideA.CommunityBan);
        Assert.Equal(10u, Assert.Single(vm.MutualGames).AppId);
    }

    [Fact]
    public async Task PrivateLibraryHasNoClaimedZeroOrMutualRows()
    {
        var libraries = new Libraries
        {
            Library = (id, _) => Task.FromResult(id == IdA
                ? Outcome<OwnedLibrary>.Failure(DataState.Private, "Private")
                : Library(Game(10, "Other", 0)))
        };
        using var vm = Create(new Profiles(), libraries);
        vm.SideA.Input = IdA;
        vm.SideB.Input = IdB;
        await vm.CompareAsync(false);
        Assert.Equal("N/A", vm.SideA.Games);
        Assert.Equal("N/A", vm.SideA.Playtime);
        Assert.Equal(DataState.Private, vm.State);
        Assert.False(vm.HasComparison);
        Assert.Empty(vm.MutualGames);
        Assert.Contains("private", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IncompleteLibraryDoesNotPresentPartialTotalsAsWholeLibrary()
    {
        var libraries = new Libraries { Library = (_, _) => Task.FromResult(Outcome<OwnedLibrary>.Ready(new([Game(10, "Partial", 60)], 999, false))) };
        using var vm = Create(new Profiles(), libraries);
        vm.SideA.Input = IdA;
        vm.SideB.Input = IdB;
        await vm.CompareAsync(false);
        Assert.Equal("N/A", vm.SideA.Games);
        Assert.Equal("N/A", vm.SideA.Playtime);
        Assert.Contains("incomplete", vm.SideA.LibraryStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DataState.Unavailable, vm.State);
        Assert.False(vm.HasComparison);
    }

    [Fact]
    public async Task KnownEmptyLibrariesShowAnHonestEmptyIntersection()
    {
        using var vm = Create(new Profiles(), new Libraries { Library = (_, _) => Task.FromResult(Library()) });
        vm.SideA.Input = IdA;
        vm.SideB.Input = IdB;
        await vm.CompareAsync(false);
        Assert.Equal(DataState.Empty, vm.State);
        Assert.True(vm.HasComparison);
        Assert.Equal("0", vm.SideA.Games);
        Assert.Contains("0", vm.SideA.Playtime);
        Assert.True(vm.ShowNoMatches);
        Assert.Contains("no mutual", vm.NoMatches, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FiltersSearchNameAndAppIdAndRequireHoursOnEachSide()
    {
        var libraries = new Libraries { Library = (id, _) => Task.FromResult(Library(
            Game(10, "Puzzle Adventure", id == IdA ? 120 : 60),
            Game(20, "Racing", id == IdA ? 600 : 30),
            Game(30, "Unknown hours", id == IdA ? null : 600))) };
        using var vm = Create(new Profiles(), libraries);
        vm.SideA.Input = IdA;
        vm.SideB.Input = IdB;
        await vm.CompareAsync(false);
        vm.Query = "PUZZLE";
        await vm.ApplyFiltersAsync();
        Assert.Equal(10u, Assert.Single(vm.MutualGames).AppId);
        vm.Query = "20";
        await vm.ApplyFiltersAsync();
        Assert.Equal(20u, Assert.Single(vm.MutualGames).AppId);
        vm.Query = "";
        vm.MinimumHours = "1";
        await vm.ApplyFiltersAsync();
        Assert.Equal(10u, Assert.Single(vm.MutualGames).AppId);
        vm.MinimumHours = "-1";
        await vm.ApplyFiltersAsync();
        Assert.True(vm.HasFilterError);
        Assert.Empty(vm.MutualGames);
        Assert.False(vm.ShowNoMatches);
    }

    [Fact]
    public async Task SortIsDeterministicAndUnknownPlaytimeRemainsNA()
    {
        var libraries = new Libraries { Library = (id, _) => Task.FromResult(Library(
            Game(10, "Zeta", 60), Game(20, "Alpha", null), Game(30, "Beta", id == IdA ? 180 : 120))) };
        using var vm = Create(new Profiles(), libraries);
        vm.SideA.Input = IdA;
        vm.SideB.Input = IdB;
        await vm.CompareAsync(false);
        Assert.Equal(new uint[] { 30, 10, 20 }, vm.MutualGames.Select(row => row.AppId));
        Assert.Equal("N/A", vm.MutualGames.Last().HoursA);
        vm.SortIndex = 1;
        await vm.ApplyFiltersAsync();
        Assert.Equal(new uint[] { 20, 30, 10 }, vm.MutualGames.Select(row => row.AppId));
    }

    [Fact]
    public async Task BanFlagAndCountRemainIndependentWhenSteamOmitsTheCount()
    {
        using var vm = Create(new Profiles
        {
            Bans = (_, _) => Task.FromResult(Outcome<SteamBans>.Ready(new(null, true, null, null, null, null)))
        }, new Libraries());
        vm.SideA.Input = IdA;
        await vm.SideA.SearchAsync(false);
        Assert.Equal("Yes", vm.SideA.VacStatus);
        Assert.Equal("N/A", vm.SideA.VacBans);
        Assert.Equal("N/A", vm.SideA.CommunityBan);
    }

    [Fact]
    public async Task MissingKeyIsActionableAndNeverThrows()
    {
        var profiles = new Profiles { Find = (_, _) => Task.FromResult(Outcome<SteamProfile>.Failure(DataState.MissingKey, "Do not expose raw details")) };
        using var vm = Create(profiles, new Libraries());
        vm.SideA.Input = IdA;
        vm.SideB.Input = IdB;
        await vm.CompareAsync(false);
        Assert.False(vm.IsBusy);
        Assert.False(vm.SideA.HasProfile);
        Assert.Contains("GAMEPULSE_STEAM_API_KEY", vm.SideA.IdentityStatus);
        Assert.False(vm.HasComparison);
    }

    [Fact]
    public async Task UnexpectedFailuresNeverExposeExceptionTextOrRequestKey()
    {
        var profiles = new Profiles { Find = (_, _) => throw new InvalidOperationException("https://api.invalid?key=SECRET_TEST_VALUE") };
        using var vm = Create(profiles, new Libraries());
        vm.SideA.Input = IdA;
        await vm.SideA.SearchAsync(false);
        Assert.DoesNotContain("SECRET_TEST_VALUE", vm.SideA.IdentityStatus);
        Assert.DoesNotContain("key=", vm.SideA.LibraryStatus);
        Assert.False(vm.SideA.IsBusy);
    }

    [Fact]
    public async Task CancellingOneSideDoesNotCancelOtherSideAndRejectsLateResponse()
    {
        var pending = Pending<SteamProfile>();
        using var vm = Create(new Profiles { Find = (input, _) => input == IdA ? pending.Task : Task.FromResult(Profile(input)) }, new Libraries());
        vm.SideA.Input = IdA;
        var a = vm.SideA.SearchAsync(false);
        vm.SideB.Input = IdB;
        await vm.SideB.SearchAsync(false);
        vm.SideA.Cancel();
        Assert.False(vm.SideA.IsBusy);
        Assert.True(vm.SideB.HasProfile);
        pending.SetResult(Profile(IdA));
        await a;
        Assert.False(vm.SideA.HasProfile);
        Assert.Equal(IdB, vm.SideB.SteamId);
        Assert.Equal(DataState.Unavailable, vm.SideA.LibraryResult!.State);
    }

    [Fact]
    public async Task CancellingBothCompletesWithoutWaitingForUncooperativeIdentityTransport()
    {
        var blocked = Pending<SteamProfile>();
        using var vm = Create(new Profiles { Find = (_, _) => blocked.Task }, new Libraries());
        vm.SideA.Input = IdA;
        vm.SideB.Input = IdB;
        var comparison = vm.CompareAsync(false);
        vm.Cancel();
        await comparison.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsBusy);
        Assert.False(vm.SideA.HasProfile);
        Assert.False(vm.SideB.HasProfile);
        Assert.False(vm.HasComparison);
        Assert.NotEqual(DataState.Loading, vm.State);
        // No completion of blocked: the cancellation contract must not depend on it.
    }

    [Fact]
    public async Task CancellingSideCompletesWithoutWaitingForUncooperativeEnrichmentTransports()
    {
        var level = Pending<ProfileLevel>();
        var bans = Pending<SteamBans>();
        var library = Pending<OwnedLibrary>();
        using var vm = Create(new Profiles { Level = (_, _) => level.Task, Bans = (_, _) => bans.Task },
            new Libraries { Library = (_, _) => library.Task });
        vm.SideA.Input = IdA;
        var search = vm.SideA.SearchAsync(false);
        Assert.True(vm.SideA.HasProfile);
        vm.SideA.Cancel();
        await search.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.SideA.IsBusy);
        Assert.Equal(DataState.Unavailable, vm.SideA.LibraryResult!.State);
        Assert.Equal("N/A", vm.SideA.Level);
    }

    [Fact]
    public async Task RefreshBypassesCacheForBothIndependentInputsAndLibraries()
    {
        var profiles = new Profiles();
        var libraries = new Libraries();
        using var vm = Create(profiles, libraries);
        vm.SideA.Input = IdA;
        vm.SideB.Input = IdB;
        await vm.CompareAsync(true);
        Assert.All(profiles.Inputs, call => Assert.True(call.Force));
        Assert.Equal(2, libraries.Calls.Count);
        Assert.All(libraries.Calls, call => Assert.True(call.Force));
    }

    [Fact]
    public async Task LargeMutualCollectionCanFilterAndRestoreAllRowsWithoutCap()
    {
        var games = Enumerable.Range(1, 10_000).Select(id => Game((uint)id, "Game " + id, id)).ToArray();
        using var vm = Create(new Profiles(), new Libraries { Library = (_, _) => Task.FromResult(Library(games)) });
        vm.SideA.Input = IdA;
        vm.SideB.Input = IdB;
        await vm.CompareAsync(false);
        Assert.Equal(10_000, vm.MutualGames.Count);
        vm.Query = "Game 10000";
        await vm.ApplyFiltersAsync();
        Assert.Equal(10_000u, Assert.Single(vm.MutualGames).AppId);
        vm.Query = "";
        await vm.ApplyFiltersAsync();
        Assert.Equal(10_000, vm.MutualGames.Count);
    }

    [Fact]
    public async Task DisposeRejectsLateLookupWithoutRepopulatingView()
    {
        var pending = Pending<SteamProfile>();
        var vm = Create(new Profiles { Find = (_, _) => pending.Task }, new Libraries());
        vm.SideA.Input = IdA;
        var search = vm.SideA.SearchAsync(false);
        vm.Dispose();
        pending.SetResult(Profile(IdA));
        await search;
        Assert.False(vm.SideA.HasProfile);
        Assert.Empty(vm.MutualGames);
    }

    private static ComparisonViewModel Create(Profiles profiles, Libraries libraries) => new(profiles, libraries, new ProfileComparisonService());
    private static TaskCompletionSource<Outcome<T>> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Outcome<SteamProfile> Profile(string id) => Outcome<SteamProfile>.Ready(new(id, "Profile " + id[^2..], "https://steamcommunity.com/profiles/" + id, null, 3, 1, null, null, null));
    private static LibraryGame Game(uint id, string name, long? minutes) => new(id, name, minutes, null, null);
    private static Outcome<OwnedLibrary> Library(params LibraryGame[] games) => new(games.Length == 0 ? DataState.Empty : DataState.Ready, new(games, games.Length, true));

    private sealed class Profiles : IProfileLookupService
    {
        public ConcurrentBag<(string Input, bool Force)> Inputs { get; } = [];
        public Func<string, CancellationToken, Task<Outcome<SteamProfile>>> Find { get; init; } = (id, _) => Task.FromResult(Profile(id));
        public Func<string, CancellationToken, Task<Outcome<ProfileLevel>>> Level { get; init; } = (_, _) => Task.FromResult(Outcome<ProfileLevel>.Ready(new(10)));
        public Func<string, CancellationToken, Task<Outcome<SteamBans>>> Bans { get; init; } = (_, _) => Task.FromResult(Outcome<SteamBans>.Ready(new(null, null, null, null, null, null)));
        public Task<Outcome<SteamProfile>> FindAsync(string input, bool forceRefresh, CancellationToken cancellationToken) { Inputs.Add((input, forceRefresh)); return Find(input, cancellationToken); }
        public Task<Outcome<ProfileLevel>> GetLevelAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => Level(steamId, cancellationToken);
        public Task<Outcome<SteamBans>> GetBansAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => Bans(steamId, cancellationToken);
    }

    private sealed class Libraries : ILibraryService
    {
        public ConcurrentBag<(string Id, bool Force)> Calls { get; } = [];
        public Func<string, CancellationToken, Task<Outcome<OwnedLibrary>>> Library { get; init; } = (_, _) => Task.FromResult(ComparisonViewModelTests.Library(Game(10, "Shared game", 60)));
        public Task<Outcome<OwnedLibrary>> GetLibraryAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) { Calls.Add((steamId, forceRefresh)); return Library(steamId, cancellationToken); }
        public Task<Outcome<RecentGames>> GetRecentAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
