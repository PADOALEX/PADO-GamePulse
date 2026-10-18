using GamePulse.Core;
using GamePulse.Desktop.Services;
using GamePulse.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class AchievementsNavigationTests
{
    private const string IdA = "76561198000000001", IdB = "76561198000000002";

    [Fact]
    public async Task ProfileSearchAndPageNavigationDoNotStartAchievementSweep()
    {
        var service = new Achievements();
        using var avatars = new AvatarService();
        var achievements = new AchievementsViewModel(new Libraries(), service);
        using var vm = new MainWindowViewModel(new Profiles(), avatars, NullLogger<MainWindowViewModel>.Instance,
            achievements: achievements);
        vm.ShowAchievementsCommand.Execute(null);
        vm.SearchInput = IdA;
        await vm.SearchAsync(false);
        Assert.True(vm.IsAchievements);
        Assert.Equal(2, achievements.LibraryRows.Count);
        Assert.Null(achievements.SelectedGame);
        Assert.Equal(0, service.Calls);
        vm.ShowGamesCommand.Execute(null);
        vm.ShowAchievementsCommand.Execute(null);
        Assert.Equal(0, service.Calls);
        achievements.SelectedGame = achievements.LibraryRows[0];
        await achievements.SelectionLoadingTask;
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task ReplacingProfileCancelsSelectedGameAndRejectsItsLateCompletion()
    {
        var late = new TaskCompletionSource<Outcome<GameAchievements>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken selectedToken = default;
        var service = new Achievements { Get = (_, app, token) => { selectedToken = token; return late.Task; } };
        using var avatars = new AvatarService();
        var achievements = new AchievementsViewModel(new Libraries(), service);
        using var vm = new MainWindowViewModel(new Profiles(), avatars, NullLogger<MainWindowViewModel>.Instance,
            achievements: achievements);
        vm.SearchInput = IdA;
        await vm.SearchAsync(false);
        achievements.SelectedGame = achievements.LibraryRows[0];
        var selection = achievements.SelectionLoadingTask;
        vm.SearchInput = IdB;
        await vm.SearchAsync(false);
        Assert.True(selectedToken.IsCancellationRequested);
        await selection.WaitAsync(TimeSpan.FromSeconds(5));
        late.SetResult(Complete(10));
        Assert.Equal(IdB, vm.SteamId);
        Assert.Null(achievements.SelectedGame);
        Assert.Empty(achievements.AchievementRows);
        Assert.Single(achievements.LibraryRows);
        Assert.Equal(30u, achievements.LibraryRows[0].AppId);
    }

    [Theory]
    [InlineData(DataState.MissingKey)]
    [InlineData(DataState.InvalidInput)]
    [InlineData(DataState.Failed)]
    public async Task GlobalLookupFailureFromFeaturePageShowsVisibleActionableStatus(DataState state)
    {
        using var avatars = new AvatarService();
        var achievements = new AchievementsViewModel(new Libraries(), new Achievements());
        using var vm = new MainWindowViewModel(new Profiles { Failure = state }, avatars,
            NullLogger<MainWindowViewModel>.Instance, achievements: achievements);
        vm.ShowAchievementsCommand.Execute(null);
        await vm.SearchAsync(false);
        Assert.True(vm.IsOverview);
        Assert.False(vm.HasProfile);
        Assert.False(vm.IsBusy);
        Assert.NotEmpty(vm.StatusMessage);
        Assert.Empty(achievements.LibraryRows);
        if (state == DataState.MissingKey) Assert.True(vm.IsSetupRequired);
    }

    [Fact]
    public async Task GlobalEscapeCancelsSelectedGameEvenAfterProfileLoadHasFinished()
    {
        CancellationToken selectedToken = default;
        var service = new Achievements
        {
            Get = (_, _, token) =>
            {
                selectedToken = token;
                return new TaskCompletionSource<Outcome<GameAchievements>>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }
        };
        using var avatars = new AvatarService();
        var achievements = new AchievementsViewModel(new Libraries(), service);
        using var vm = new MainWindowViewModel(new Profiles(), avatars, NullLogger<MainWindowViewModel>.Instance,
            achievements: achievements);
        vm.ShowAchievementsCommand.Execute(null);
        vm.SearchInput = IdA;
        await vm.SearchAsync(false);
        Assert.False(vm.IsBusy);
        achievements.SelectedGame = achievements.LibraryRows[0];
        var selection = achievements.SelectionLoadingTask;
        vm.Cancel();
        await selection.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(selectedToken.IsCancellationRequested);
        Assert.False(achievements.IsSelectionBusy);
    }

    private static Outcome<GameAchievements> Complete(uint app) => Outcome<GameAchievements>.Ready(new(app, "Test game",
        [new("TEST", "Test achievement", "Test description", false, true, null)], true));
    private sealed class Achievements : IAchievementService
    {
        public int Calls { get; private set; }
        public Func<string, uint, CancellationToken, Task<Outcome<GameAchievements>>> Get { get; init; } =
            (_, app, _) => Task.FromResult(Complete(app));
        public Task<Outcome<GameAchievements>> GetAchievementsAsync(string id, uint app, bool force, CancellationToken token)
        { Calls++; return Get(id, app, token); }
    }
    private sealed class Libraries : ILibraryService
    {
        public Task<Outcome<OwnedLibrary>> GetLibraryAsync(string id, bool force, CancellationToken token) => Task.FromResult(
            Outcome<OwnedLibrary>.Ready(new(id == IdA
                ? [new(10, "Test A", 1, null, null), new(20, "Test B", 1, null, null)]
                : [new(30, "Test C", 1, null, null)], id == IdA ? 2 : 1, true)));
        public Task<Outcome<RecentGames>> GetRecentAsync(string id, bool force, CancellationToken token) =>
            Task.FromResult(Outcome<RecentGames>.Ready(new([], 0)));
    }
    private sealed class Profiles : IProfileLookupService
    {
        public DataState? Failure { get; init; }
        public Task<Outcome<SteamProfile>> FindAsync(string input, bool force, CancellationToken token) => Task.FromResult(
            Failure.HasValue ? Outcome<SteamProfile>.Failure(Failure.Value, "Test source failure") :
            Outcome<SteamProfile>.Ready(new(input, "Test profile", "https://steamcommunity.com/profiles/" + input,
                null, 3, 1, null, null, null)));
        public Task<Outcome<ProfileLevel>> GetLevelAsync(string id, bool force, CancellationToken token) =>
            Task.FromResult(Outcome<ProfileLevel>.Failure(DataState.Unavailable, "Test level unavailable"));
        public Task<Outcome<SteamBans>> GetBansAsync(string id, bool force, CancellationToken token) =>
            Task.FromResult(Outcome<SteamBans>.Failure(DataState.Unavailable, "Test bans unavailable"));
    }
}
