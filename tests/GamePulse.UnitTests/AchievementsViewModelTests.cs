using GamePulse.Core;
using GamePulse.Desktop.Resources;
using GamePulse.Desktop.ViewModels;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class AchievementsViewModelTests
{
    [Fact]
    public async Task LibraryAndFilteringDoNotRequestAchievementsOrSelectFirstGame()
    {
        var service = new Achievements(); using var vm = Create(service);
        await vm.LoadAsync("A", false, default);
        Assert.Equal(3, vm.LibraryRows.Count); Assert.Null(vm.SelectedGame); Assert.Equal(0, service.Requests);
        vm.GameQuery = "Game"; await vm.ApplyGameFiltersAsync(); vm.Query = "First"; await vm.ApplyFiltersAsync();
        Assert.Equal(0, service.Requests); Assert.Equal("N/A", vm.WeightedCompletion); Assert.Equal("0 / 3", vm.Coverage);
    }

    [Fact]
    public async Task SelectingOneGameRequestsOnlyItAndReselectingSameObjectDoesNotRepeat()
    {
        var service = new Achievements(); using var vm = Create(service); await vm.LoadAsync("A", false, default);
        var row = vm.LibraryRows.Single(x => x.AppId == 2); vm.SelectedGame = row; await vm.SelectionLoadingTask;
        Assert.Equal(1, service.Requests); Assert.Equal(("A", 2u, false), service.Calls.Single());
        vm.SelectedGame = row; await vm.SelectionLoadingTask; await vm.ApplyGameFiltersAsync();
        Assert.Equal(1, service.Requests); Assert.Equal(2, vm.AchievementRows.Count); Assert.Equal("50%", vm.SelectedProgress);
        Assert.Equal("1", vm.SelectedUnlocked); Assert.Equal("2", vm.SelectedTotal); Assert.Equal("1 / 3", vm.Coverage);
    }

    [Fact]
    public async Task RevisitingSuccessfulGameUsesServiceCacheRatherThanRetainingUnboundedDetailPayloads()
    {
        var service = new Achievements(); using var vm = Create(service); await vm.LoadAsync("A", false, default);
        vm.SelectedGame = vm.LibraryRows[0]; await vm.SelectionLoadingTask;
        vm.SelectedGame = vm.LibraryRows[1]; await vm.SelectionLoadingTask;
        vm.SelectedGame = vm.LibraryRows[0]; await vm.SelectionLoadingTask;
        Assert.Equal(3, service.Requests); Assert.All(service.Calls, x => Assert.False(x.Force));
        Assert.Equal("2 / 3", vm.Coverage);
    }

    [Theory]
    [InlineData(DataState.Private, "private")]
    [InlineData(DataState.Unavailable, "unavailable")]
    [InlineData(DataState.Failed, "could not be loaded")]
    [InlineData(DataState.MissingKey, "API key")]
    [InlineData(DataState.RateLimited, "limited")]
    [InlineData(DataState.NoAchievements, "no achievements")]
    public async Task DistinctStatesNeverBecomeZeroCompletionAndAreNotImplicitlyRetried(DataState state, string message)
    {
        var service = new Achievements { Get = (_, _, _, _) => Task.FromResult(Outcome<GameAchievements>.Failure(state, "Secret response text")) };
        using var vm = Create(service); await vm.LoadAsync("A", false, default);
        var chosen = vm.LibraryRows[0]; vm.SelectedGame = chosen; await vm.SelectionLoadingTask;
        Assert.Equal(state, vm.SelectedState); Assert.Contains(message, vm.SelectedMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("N/A", vm.SelectedProgress); Assert.Equal("N/A", vm.WeightedCompletion); Assert.Empty(vm.AchievementRows);
        Assert.Equal(state == DataState.NoAchievements ? "0" : "N/A", vm.SelectedTotal);
        vm.SelectedGame = null; vm.SelectedGame = chosen; await vm.SelectionLoadingTask;
        Assert.Equal(1, service.Requests); Assert.Equal(1, vm.CheckedGameCount);
        await vm.RefreshSelectedAsync(); Assert.Equal(2, service.Requests); Assert.True(service.Calls.Last().Force);
    }

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(2, "100%")]
    public async Task ZeroAndCompleteProgressAreRealResults(int unlocked, string expected)
    {
        using var vm = Create(new Achievements { Get = (_, app, _, _) => Task.FromResult(Result(app, unlocked, 2)) });
        await vm.LoadAsync("A", false, default); vm.SelectedGame = vm.LibraryRows[0]; await vm.SelectionLoadingTask;
        Assert.True(vm.HasSelectedProgress); Assert.Equal(expected, vm.SelectedProgress); Assert.Equal(expected, vm.WeightedCompletion);
        Assert.Equal(1, vm.IncludedGames); Assert.Equal(unlocked.ToString(), vm.SelectedUnlocked);
    }

    [Fact]
    public async Task PartialUnknownRowsRemainVisibleWithoutInventedTotalsOrUnlockFlags()
    {
        var detail = new AchievementDetail("UNKNOWN", null, null, null, null, null);
        using var vm = Create(new Achievements { Get = (_, app, _, _) => Task.FromResult(Outcome<GameAchievements>.Ready(new(app, null, [detail], false))) });
        await vm.LoadAsync("A", false, default); vm.SelectedGame = vm.LibraryRows[0]; await vm.SelectionLoadingTask;
        Assert.Equal(DataState.Ready, vm.SelectedState); Assert.Contains("incomplete", vm.SelectedMessage);
        Assert.Equal("N/A", vm.SelectedTotal); Assert.Equal("N/A", vm.SelectedUnlocked); Assert.Equal("N/A", vm.SelectedProgress);
        var row = Assert.Single(vm.AchievementRows); Assert.Equal("UNKNOWN", row.Name); Assert.Equal("Unknown", row.Status);
        Assert.Equal("Unknown", row.Hidden); Assert.Equal("Unknown", row.UnlockedAt); Assert.Contains("did not return", row.Description);
        Assert.Equal(0, vm.IncludedGames); Assert.Equal("1 / 3", vm.Coverage);
        vm.SelectedUnlockFilterIndex = 2; await vm.ApplyFiltersAsync(); Assert.Empty(vm.AchievementRows);
    }

    [Fact]
    public async Task WeightedAndAverageAreDifferentAndExcludeUnknownNoAchievementAndPrivateGames()
    {
        var service = new Achievements { Get = (_, app, _, _) => Task.FromResult(app switch
        {
            1 => Result(app, 1, 1), 2 => Result(app, 1, 3),
            _ => Outcome<GameAchievements>.Failure(DataState.NoAchievements, "No achievements")
        }) };
        using var vm = Create(service); await vm.LoadAsync("A", false, default);
        foreach (var game in vm.LibraryRows) { vm.SelectedGame = game; await vm.SelectionLoadingTask; }
        Assert.Equal("50%", vm.WeightedCompletion);
        Assert.Equal(AchievementsText.Format("Percent", 66.7), vm.AverageCompletion);
        Assert.Equal(2, vm.IncludedGames); Assert.Equal(3, vm.CheckedGameCount); Assert.Equal("3 / 3", vm.Coverage);
    }

    [Fact]
    public async Task ForceRefreshClearsOldCountsWhilePendingAndFailureDoesNotRestoreOldCompletion()
    {
        var pending = Pending<Outcome<GameAchievements>>(); var next = false;
        var service = new Achievements { Get = (_, app, _, _) => next ? pending.Task : Task.FromResult(Result(app, 2, 2)) };
        using var vm = Create(service); await vm.LoadAsync("A", false, default); vm.SelectedGame = vm.LibraryRows[0]; await vm.SelectionLoadingTask;
        Assert.Equal("100%", vm.WeightedCompletion); next = true;
        var refresh = vm.RefreshSelectedAsync(); Assert.True(vm.IsSelectionBusy); Assert.Equal("N/A", vm.WeightedCompletion);
        Assert.Equal("N/A", vm.SelectedTotal); Assert.Equal(0, vm.CheckedGameCount);
        pending.SetResult(Outcome<GameAchievements>.Failure(DataState.Failed, "Safe failure")); await refresh;
        Assert.Equal("N/A", vm.SelectedProgress); Assert.Equal(0, vm.IncludedGames); Assert.Equal(1, vm.CheckedGameCount);
    }

    [Fact]
    public async Task SelectionRaceCancelsAndSuppressesNonCooperativeOlderResponse()
    {
        var old = Pending<Outcome<GameAchievements>>(); CancellationToken oldToken = default;
        var service = new Achievements { Get = (_, app, _, token) => { if (app == 1) { oldToken = token; return old.Task; } return Task.FromResult(Result(app, 2, 2)); } };
        using var vm = Create(service); await vm.LoadAsync("A", false, default);
        vm.SelectedGame = vm.LibraryRows.Single(x => x.AppId == 1); var first = vm.SelectionLoadingTask;
        vm.SelectedGame = vm.LibraryRows.Single(x => x.AppId == 2); await vm.SelectionLoadingTask;
        Assert.True(oldToken.IsCancellationRequested); old.SetResult(Result(1, 0, 2)); await first;
        Assert.Equal(2u, vm.SelectedGame!.AppId); Assert.Equal("100%", vm.SelectedProgress); Assert.Equal(1, vm.CheckedGameCount);
        Assert.False(vm.LibraryRows.Single(x => x.AppId == 1).IsLoading);
    }

    [Fact]
    public async Task ProfileChangeDropsAllOldCoverageAndIgnoresLateSelectedPayload()
    {
        var old = Pending<Outcome<GameAchievements>>();
        using var vm = Create(new Achievements { Get = (_, _, _, _) => old.Task });
        await vm.LoadAsync("A", false, default); vm.SelectedGame = vm.LibraryRows[0]; var loading = vm.SelectionLoadingTask;
        await vm.LoadAsync("B", false, default); old.SetResult(Result(1, 2, 2)); await loading;
        Assert.Null(vm.SelectedGame); Assert.Empty(vm.AchievementRows); Assert.Equal(0, vm.CheckedGameCount); Assert.Equal("N/A", vm.WeightedCompletion);
    }

    [Fact]
    public async Task CancelSelectedCompletesPromptlyEvenWhenServiceIgnoresToken()
    {
        var pending = Pending<Outcome<GameAchievements>>(); CancellationToken token = default;
        using var vm = Create(new Achievements { Get = (_, _, _, cancellation) => { token = cancellation; return pending.Task; } });
        await vm.LoadAsync("A", false, default); vm.SelectedGame = vm.LibraryRows[0]; var load = vm.SelectionLoadingTask;
        vm.CancelSelected(); await load.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(token.IsCancellationRequested); Assert.False(vm.IsSelectionBusy); Assert.Contains("cancelled", vm.SelectedMessage);
        Assert.Equal("N/A", vm.SelectedProgress); Assert.Equal(0, vm.CheckedGameCount);
        pending.SetResult(Result(1, 2, 2)); Assert.Empty(vm.AchievementRows);
    }

    [Fact]
    public async Task SearchingNamesDescriptionsAndApiNamesAndUnlockFilterUsesAllRows()
    {
        using var vm = Create(); await vm.LoadAsync("A", false, default); vm.SelectedGame = vm.LibraryRows[0]; await vm.SelectionLoadingTask;
        vm.Query = "special description"; await vm.ApplyFiltersAsync(); Assert.Equal("A_0", Assert.Single(vm.AchievementRows).ApiName);
        vm.Query = "A_1"; await vm.ApplyFiltersAsync(); Assert.Equal("Locked", Assert.Single(vm.AchievementRows).Status);
        vm.Query = "Achievement"; vm.SelectedUnlockFilterIndex = 1; await vm.ApplyFiltersAsync(); Assert.Equal("Unlocked", Assert.Single(vm.AchievementRows).Status);
        vm.SelectedUnlockFilterIndex = 2; await vm.ApplyFiltersAsync(); Assert.Equal("Locked", Assert.Single(vm.AchievementRows).Status);
        vm.Query = "nothing"; await vm.ApplyFiltersAsync(); Assert.Empty(vm.AchievementRows); Assert.Contains("No achievements match", vm.SelectedMessage);
    }

    [Fact]
    public async Task NewSelectionDuringDebounceIsPreservedAndFilteredOutSelectionIsCleared()
    {
        using var vm = Create(); await vm.LoadAsync("A", false, default); vm.SelectedGame = vm.LibraryRows[0]; await vm.SelectionLoadingTask;
        vm.SelectedAchievement = vm.AchievementRows[0]; vm.Query = "Achievement"; var filter = vm.FilteringTask;
        var latest = vm.AchievementRows[1]; vm.SelectedAchievement = latest; await filter;
        Assert.Equal(latest.ApiName, vm.SelectedAchievement?.ApiName);
        vm.Query = "nothing"; await vm.ApplyFiltersAsync(); Assert.Null(vm.SelectedAchievement);
        vm.GameQuery = "Game"; var games = vm.GameFilteringTask; var latestGame = vm.LibraryRows[1]; vm.SelectedGame = latestGame;
        await vm.SelectionLoadingTask; await games; Assert.Same(latestGame, vm.SelectedGame);
        vm.GameQuery = "no games"; await vm.ApplyGameFiltersAsync(); Assert.Null(vm.SelectedGame); Assert.Empty(vm.AchievementRows);
    }

    [Fact]
    public void DetailDatesRespectKnownUnlockStateAndHiddenFlag()
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var known = new AchievementRow(new("KNOWN", "Known", "Details", true, true, date));
        Assert.Equal("Yes", known.Hidden); Assert.NotEqual("Unknown", known.UnlockedAt);
        Assert.Equal("Unknown", (known with { Achievement = known.Achievement with { UnlockedAt = null } }).UnlockedAt);
        Assert.Equal("Not unlocked", (known with { Achievement = known.Achievement with { Unlocked = false } }).UnlockedAt);
        Assert.Equal("Unknown", (known with { Achievement = known.Achievement with { Unlocked = null } }).UnlockedAt);
    }

    [Theory]
    [InlineData(DataState.Private)]
    [InlineData(DataState.Unavailable)]
    [InlineData(DataState.Failed)]
    [InlineData(DataState.MissingKey)]
    [InlineData(DataState.RateLimited)]
    public async Task InaccessibleLibraryHasUnknownCoverageAndMakesNoAchievementRequests(DataState state)
    {
        var service = new Achievements(); using var vm = new AchievementsViewModel(new Libraries { Get = (_, _, _) => Task.FromResult(Outcome<OwnedLibrary>.Failure(state, "Safe")) }, service);
        await vm.LoadAsync("A", false, default); Assert.Equal(state, vm.LibraryState); Assert.Empty(vm.LibraryRows);
        Assert.Equal("N/A", vm.Coverage); Assert.Equal(0, service.Requests); Assert.False(vm.IsLibraryBusy);
    }

    [Fact]
    public async Task EmptyLibraryDiffersFromUnavailableAndIncompleteLibraryHasCoverageWarning()
    {
        using var empty = new AchievementsViewModel(new Libraries { Get = (_, _, _) => Task.FromResult(new Outcome<OwnedLibrary>(DataState.Empty, new([], 0, true))) }, new Achievements());
        await empty.LoadAsync("A", false, default); Assert.Contains("empty library", empty.LibraryMessage); Assert.Equal("0 / 0", empty.Coverage);
        using var partial = new AchievementsViewModel(new Libraries { Get = (_, _, _) => Task.FromResult(Outcome<OwnedLibrary>.Ready(new([Game(1)], 5, false))) }, new Achievements());
        await partial.LoadAsync("A", false, default); Assert.True(partial.HasLibraryCompleteness); Assert.Contains("incomplete", partial.LibraryCompleteness);
    }

    [Fact]
    public async Task LargeLibraryAndAchievementSourcesHaveNoHiddenTruncation()
    {
        var service = new Achievements { Get = (_, app, _, _) => Task.FromResult(Result(app, 1000, 12000)) };
        using var vm = new AchievementsViewModel(new Libraries { Get = (_, _, _) => Task.FromResult(Outcome<OwnedLibrary>.Ready(new(Enumerable.Range(1, 12000).Select(x => Game((uint)x)).ToArray(), 12000, true))) }, service);
        await vm.LoadAsync("A", false, default); Assert.Equal(12000, vm.LibraryRows.Count); Assert.Equal(0, service.Requests);
        vm.GameQuery = "12000"; await vm.ApplyGameFiltersAsync(); vm.SelectedGame = Assert.Single(vm.LibraryRows); await vm.SelectionLoadingTask;
        Assert.Equal(12000, vm.AchievementRows.Count); vm.Query = "A_11999"; await vm.ApplyFiltersAsync(); Assert.Single(vm.AchievementRows);
    }

    [Fact]
    public async Task ClearAndDisposeCancelPendingWorkWithoutStaleDataAndDoNotExposeTransportDetails()
    {
        var pending = Pending<Outcome<GameAchievements>>(); CancellationToken captured = default;
        var vm = Create(new Achievements { Get = (_, _, _, token) => { captured = token; return pending.Task; } });
        await vm.LoadAsync("A", false, default); vm.SelectedGame = vm.LibraryRows[0]; var load = vm.SelectionLoadingTask;
        vm.Query = "Achievement"; var filtering = vm.FilteringTask; vm.Dispose(); await load; await filtering;
        Assert.True(captured.IsCancellationRequested); Assert.Empty(vm.LibraryRows); Assert.Empty(vm.AchievementRows); Assert.Null(vm.SelectedGame);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => vm.LoadAsync("B", false, default));
        Assert.False(vm.IsFiltering); pending.SetResult(Result(1, 2, 2));
        using var failed = Create(new Achievements { Get = (_, _, _, _) => throw new HttpRequestException("SECRET key=DO_NOT_SHOW") });
        await failed.LoadAsync("A", false, default); failed.SelectedGame = failed.LibraryRows[0]; await failed.SelectionLoadingTask;
        Assert.Equal(DataState.Failed, failed.SelectedState); Assert.DoesNotContain("SECRET", failed.SelectedMessage); Assert.DoesNotContain("key=", failed.SelectedMessage);
    }

    [Fact]
    public async Task LibraryCancellationAndNonCooperativeOldProfileAreSuppressed()
    {
        var pending = Pending<Outcome<OwnedLibrary>>(); CancellationToken captured = default;
        using var vm = new AchievementsViewModel(new Libraries { Get = (id, _, token) => { if (id == "A") { captured = token; return pending.Task; } return Task.FromResult(Library()); } }, new Achievements());
        var old = vm.LoadAsync("A", false, default); await vm.LoadAsync("B", false, default);
        pending.SetResult(Outcome<OwnedLibrary>.Ready(new([Game(100)], 1, true))); await old;
        Assert.True(captured.IsCancellationRequested); Assert.Equal(3, vm.LibraryRows.Count); Assert.DoesNotContain(vm.LibraryRows, x => x.AppId == 100);
    }

    private static AchievementsViewModel Create(Achievements? achievements = null) => new(new Libraries(), achievements ?? new());
    private static LibraryGame Game(uint app) => new(app, $"Game {app:00000}", app, null, null);
    private static Outcome<OwnedLibrary> Library() => Outcome<OwnedLibrary>.Ready(new([Game(1), Game(2), Game(3)], 3, true));
    private static Outcome<GameAchievements> Result(uint app, int unlocked = 1, int total = 2) => Outcome<GameAchievements>.Ready(new(app, "Game", Enumerable.Range(0, total)
        .Select(x => new AchievementDetail($"A_{x}", $"Achievement {x:00000}", x == 0 ? "A special description" : "Another description", x % 2 == 0, x < unlocked,
            x < unlocked ? DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + x) : null)).ToArray(), true));
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Libraries : ILibraryService
    {
        public Func<string, bool, CancellationToken, Task<Outcome<OwnedLibrary>>> Get { get; init; } = (_, _, _) => Task.FromResult(Library());
        public Task<Outcome<OwnedLibrary>> GetLibraryAsync(string id, bool force, CancellationToken token) => Get(id, force, token);
        public Task<Outcome<RecentGames>> GetRecentAsync(string id, bool force, CancellationToken token) => throw new InvalidOperationException("Recent games must not be fetched by achievements.");
    }
    private sealed class Achievements : IAchievementService
    {
        public int Requests { get; private set; }
        public List<(string Id, uint App, bool Force)> Calls { get; } = [];
        public Func<string, uint, bool, CancellationToken, Task<Outcome<GameAchievements>>> Get { get; init; } = (_, app, _, _) => Task.FromResult(Result(app));
        public Task<Outcome<GameAchievements>> GetAchievementsAsync(string id, uint app, bool force, CancellationToken token)
        { ++Requests; Calls.Add((id, app, force)); return Get(id, app, force, token); }
    }
}
