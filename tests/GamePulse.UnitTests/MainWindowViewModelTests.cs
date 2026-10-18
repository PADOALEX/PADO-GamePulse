using GamePulse.Core;
using GamePulse.Desktop.Services;
using GamePulse.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GamePulse.UnitTests;

public class MainWindowViewModelTests
{
    private const string IdA = "76561198000000001";
    private const string IdB = "76561198000000002";

    [Fact]
    public async Task IdentityAppearsWhileIndependentEnrichmentIsStillLoading()
    {
        var level = Pending<ProfileLevel>();
        var bans = Pending<SteamBans>();
        var service = new Profiles
        {
            Find = (_, _) => Task.FromResult(Profile(IdA)),
            Level = (_, _) => level.Task,
            Bans = (_, _) => bans.Task
        };
        using var avatars = new AvatarService();
        using var vm = Create(service, avatars);
        vm.SearchInput = IdA;
        var search = vm.SearchAsync(false);
        Assert.True(vm.HasProfile);
        Assert.Equal(IdA, vm.SteamId);
        Assert.True(vm.IsBusy);
        Assert.False(search.IsCompleted);
        level.SetResult(Outcome<ProfileLevel>.Failure(DataState.Unavailable, "Not exposed"));
        bans.SetResult(Outcome<SteamBans>.Ready(new(null, null, null, null, null, null)));
        await search.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.HasProfile);
        Assert.Equal("N/A", vm.Level);
        Assert.Equal("N/A", vm.VacBans);
        Assert.Equal("N/A", vm.CommunityBan);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task OldIdentityResponseCannotReplaceNewerProfile()
    {
        var old = Pending<SteamProfile>();
        CancellationToken tokenA = default;
        var service = new Profiles
        {
            Find = (input, token) =>
            {
                if (input != IdA) return Task.FromResult(Profile(IdB));
                tokenA = token;
                return old.Task; // Deliberately ignore cancellation to exercise the generation guard.
            }
        };
        using var avatars = new AvatarService();
        using var vm = Create(service, avatars);
        vm.SearchInput = IdA;
        var a = vm.SearchAsync(false);
        vm.SearchInput = IdB;
        await vm.SearchAsync(false);
        Assert.True(tokenA.IsCancellationRequested);
        old.SetResult(Profile(IdA));
        await a;
        Assert.Equal(IdB, vm.SteamId);
        Assert.Single(vm.RecentProfiles);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task OldEnrichmentCannotOverwriteNewProfileFacts()
    {
        var oldLevel = Pending<ProfileLevel>();
        var oldBans = Pending<SteamBans>();
        var service = new Profiles
        {
            Find = (input, _) => Task.FromResult(Profile(input)),
            Level = (id, _) => id == IdA ? oldLevel.Task : Task.FromResult(Outcome<ProfileLevel>.Ready(new(42))),
            Bans = (id, _) => id == IdA ? oldBans.Task : Task.FromResult(Outcome<SteamBans>.Ready(new(false, false, 0, 0, null, "none")))
        };
        using var avatars = new AvatarService();
        using var vm = Create(service, avatars);
        vm.SearchInput = IdA;
        var a = vm.SearchAsync(false);
        vm.SearchInput = IdB;
        await vm.SearchAsync(false);
        oldLevel.SetResult(Outcome<ProfileLevel>.Ready(new(999)));
        oldBans.SetResult(Outcome<SteamBans>.Ready(new(true, true, 9, 9, 2, "banned")));
        await a;
        Assert.Equal(IdB, vm.SteamId);
        Assert.Equal("42", vm.Level);
        Assert.Equal("0", vm.VacBans);
        Assert.Equal("No", vm.CommunityBan);
    }

    [Fact]
    public async Task MissingKeyIsAnActionableSetupStateWithoutAProfile()
    {
        var service = new Profiles { Find = (_, _) => Task.FromResult(Outcome<SteamProfile>.Failure(DataState.MissingKey, "Configure key")) };
        using var avatars = new AvatarService();
        using var vm = Create(service, avatars);
        await vm.SearchAsync(false);
        Assert.True(vm.IsSetupRequired);
        Assert.False(vm.HasProfile);
        Assert.False(vm.IsBusy);
        Assert.Contains("GAMEPULSE_STEAM_API_KEY", vm.StatusMessage);
    }

    [Theory]
    [InlineData(0, "Offline")]
    [InlineData(1, "Online")]
    [InlineData(2, "Busy")]
    [InlineData(3, "Away")]
    [InlineData(4, "Snooze")]
    [InlineData(5, "Looking to trade")]
    [InlineData(6, "Looking to play")]
    [InlineData(null, "N/A")]
    public async Task PresencePreservesAllKnownStatesAndUnknown(int? state, string expected)
    {
        var service = new Profiles { Find = (_, _) => Task.FromResult(Profile(IdA, state)) };
        using var avatars = new AvatarService();
        using var vm = Create(service, avatars);
        await vm.SearchAsync(false);
        Assert.Equal(expected, vm.Presence, ignoreCase: true);
    }

    [Fact]
    public async Task CancellationClearsBusyAndRejectsLateCompletion()
    {
        var pending = Pending<SteamProfile>();
        var service = new Profiles { Find = (_, _) => pending.Task };
        using var avatars = new AvatarService();
        using var vm = Create(service, avatars);
        var search = vm.SearchAsync(false);
        vm.Cancel();
        Assert.False(vm.IsBusy);
        pending.SetResult(Profile(IdA));
        await search;
        Assert.False(vm.HasProfile);
    }

    [Fact]
    public async Task RecentsAreBoundedAndDeduplicated()
    {
        var service = new Profiles { Find = (input, _) => Task.FromResult(Profile(input)) };
        using var avatars = new AvatarService();
        using var vm = Create(service, avatars);
        for (var i = 1; i <= 10; i++)
        {
            vm.SearchInput = (76561198000000000UL + (ulong)i).ToString();
            await vm.SearchAsync(false);
        }
        Assert.Equal(8, vm.RecentProfiles.Count);
        vm.SearchInput = IdB;
        await vm.SearchAsync(false);
        Assert.Equal(8, vm.RecentProfiles.Count);
        Assert.Equal(IdB, vm.RecentProfiles[0].SteamId);
        Assert.Single(vm.RecentProfiles, x => x.SteamId == IdB);
    }

    private static MainWindowViewModel Create(Profiles service, AvatarService avatars) => new(service, avatars, NullLogger<MainWindowViewModel>.Instance);
    private static TaskCompletionSource<Outcome<T>> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Outcome<SteamProfile> Profile(string id, int? state = 1) => Outcome<SteamProfile>.Ready(new(id, "Test profile", "https://steamcommunity.com/profiles/" + id, null, 3, state, null, null, null));

    private sealed class Profiles : IProfileLookupService
    {
        public Func<string, CancellationToken, Task<Outcome<SteamProfile>>> Find { get; init; } = (_, _) => Task.FromResult(Profile(IdA));
        public Func<string, CancellationToken, Task<Outcome<ProfileLevel>>> Level { get; init; } = (_, _) => Task.FromResult(Outcome<ProfileLevel>.Ready(new(1)));
        public Func<string, CancellationToken, Task<Outcome<SteamBans>>> Bans { get; init; } = (_, _) => Task.FromResult(Outcome<SteamBans>.Ready(new(null, null, null, null, null, null)));
        public Task<Outcome<SteamProfile>> FindAsync(string input, bool forceRefresh, CancellationToken cancellationToken) => Find(input, cancellationToken);
        public Task<Outcome<ProfileLevel>> GetLevelAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => Level(steamId, cancellationToken);
        public Task<Outcome<SteamBans>> GetBansAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => Bans(steamId, cancellationToken);
    }
}
