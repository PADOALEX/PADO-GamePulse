using GamePulse.Core;
using GamePulse.Desktop.ViewModels;
using Xunit;

namespace GamePulse.UnitTests;

public class GamesViewModelTests
{
    [Fact]
    public async Task LibraryAppearsBeforeRecentAndCarriesIndependentStates()
    {
        var recent = Pending<RecentGames>();
        using var vm = new GamesViewModel(new Libraries { Recent = (_, _) => recent.Task });
        var load = vm.LoadAsync("A", false, default);
        await vm.ApplyFiltersAsync();
        Assert.Equal(DataState.Ready, vm.OwnedState);
        Assert.Equal(DataState.Loading, vm.RecentState);
        Assert.True(vm.IsBusy);
        Assert.Equal(3, vm.Rows.Count);
        Assert.Contains("3 / 3", vm.AvailableCount);
        recent.SetResult(Outcome<RecentGames>.Failure(DataState.Private, "Access denied"));
        await load;
        Assert.Equal(DataState.Private, vm.RecentState);
        Assert.Equal(DataState.Ready, vm.OwnedState);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task NewProfileCancelsAndSuppressesOldLibraryAndRecentResponses()
    {
        var owned = Pending<OwnedLibrary>();
        var recent = Pending<RecentGames>();
        CancellationToken oldToken = default;
        using var vm = new GamesViewModel(new Libraries
        {
            Owned = (id, token) => { if (id == "A") { oldToken = token; return owned.Task; } return Task.FromResult(Library(99)); },
            Recent = (id, _) => id == "A" ? recent.Task : Task.FromResult(Outcome<RecentGames>.Ready(new([], 0)))
        });
        var a = vm.LoadAsync("A", false, default);
        await vm.LoadAsync("B", false, default);
        Assert.True(oldToken.IsCancellationRequested);
        owned.SetResult(Library(7));
        recent.SetResult(Outcome<RecentGames>.Ready(new([new(7, "Old", 99, 5, null)], 1)));
        await a;
        Assert.Equal(99u, Assert.Single(vm.Rows).AppId);
        vm.ShowRecent = true;
        await vm.ApplyFiltersAsync();
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task ClearImmediatelyRemovesRowsAndCancelsPendingFilters()
    {
        using var vm = new GamesViewModel(new Libraries());
        await vm.LoadAsync("A", false, default);
        vm.Query = "Counter";
        vm.Clear();
        await vm.ApplyFiltersAsync();
        Assert.Empty(vm.Rows);
        Assert.Null(vm.SelectedGame);
        Assert.Equal("N/A", vm.TotalHours);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SortingAndMatchingFilterKeepLatestSelectionDespiteItemsSourceBindingClear()
    {
        using var vm = new GamesViewModel(new Libraries());
        await vm.LoadAsync("A", false, default);
        var selected = vm.Rows[1];
        vm.SelectedGame = selected;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.Rows)) vm.SelectedGame = null;
        };
        vm.SelectedSortIndex = 1;
        await vm.ApplyFiltersAsync();
        Assert.Same(selected, vm.SelectedGame);
        Assert.True(vm.HasSelection);
        vm.Query = selected.Name;
        await vm.ApplyFiltersAsync();
        Assert.Same(selected, vm.SelectedGame);
        vm.Query = "A name absent from this fixture";
        await vm.ApplyFiltersAsync();
        Assert.Null(vm.SelectedGame);
        Assert.False(vm.HasSelection);
    }

    [Fact]
    public async Task MissingAndPartialPlaytimeDoNotBecomeCompleteTotals()
    {
        using var vm = new GamesViewModel(new Libraries
        {
            Owned = (_, _) => Task.FromResult(Outcome<OwnedLibrary>.Ready(new([new(1, "One", 0, null, null), new(2, "Two", null, null, null)], 2, true)))
        });
        await vm.LoadAsync("A", false, default);
        Assert.Equal("N/A", vm.TotalHours);
        Assert.Equal("N/A", vm.UnplayedCount);
        Assert.Contains("1 / 2", vm.PlaytimeCoverage);
        vm.MaximumHours = "0";
        await vm.ApplyFiltersAsync();
        Assert.Equal(1u, Assert.Single(vm.Rows).AppId);
    }

    [Fact]
    public async Task IncompleteLibraryCannotClaimTotalOrNeverPlayedCount()
    {
        using var vm = new GamesViewModel(new Libraries
        {
            Owned = (_, _) => Task.FromResult(Outcome<OwnedLibrary>.Ready(new([new(1, "One", 0, null, null)], 10, false)))
        });
        await vm.LoadAsync("A", false, default);
        Assert.Equal("N/A", vm.TotalHours);
        Assert.Equal("N/A", vm.UnplayedCount);
        Assert.False(vm.CanFilterUnplayed);
        Assert.Contains("Partial", vm.LibraryCompleteness);
        vm.UnplayedOnly = true;
        await vm.ApplyFiltersAsync();
        Assert.True(vm.HasFilterError);
        Assert.Empty(vm.Rows);
    }

    [Theory]
    [InlineData(DataState.Private)]
    [InlineData(DataState.Unavailable)]
    [InlineData(DataState.MissingKey)]
    [InlineData(DataState.RateLimited)]
    [InlineData(DataState.Failed)]
    public async Task UnavailableLibraryIsNeverShownAsZeroOrEmpty(DataState state)
    {
        using var vm = new GamesViewModel(new Libraries { Owned = (_, _) => Task.FromResult(Outcome<OwnedLibrary>.Failure(state, "Safe failure")) });
        await vm.LoadAsync("A", false, default);
        Assert.Equal(state, vm.OwnedState);
        Assert.Equal("N/A", vm.AvailableCount);
        Assert.Equal("N/A", vm.TotalHours);
        Assert.Equal("N/A", vm.UnplayedCount);
        Assert.DoesNotContain("empty", vm.OwnedStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActuallyEmptyLibraryHasKnownZeroTotals()
    {
        using var vm = new GamesViewModel(new Libraries
        {
            Owned = (_, _) => Task.FromResult(new Outcome<OwnedLibrary>(DataState.Empty, new([], 0, true)))
        });
        await vm.LoadAsync("A", false, default);
        Assert.Equal(DataState.Empty, vm.OwnedState);
        Assert.Contains("0 / 0", vm.AvailableCount);
        Assert.Equal("0", vm.UnplayedCount);
        Assert.DoesNotContain("N/A", vm.TotalHours);
        Assert.Contains("empty", vm.OwnedStatus);
    }

    [Fact]
    public async Task FilterValidationIsExplicitAndResetRestoresAllRows()
    {
        using var vm = new GamesViewModel(new Libraries());
        await vm.LoadAsync("A", false, default);
        vm.MinimumHours = "10";
        vm.MaximumHours = "2";
        await vm.ApplyFiltersAsync();
        Assert.True(vm.HasFilterError);
        Assert.Empty(vm.Rows);
        Assert.Contains("Minimum", vm.FilterError);
        vm.ResetFiltersCommand.Execute(null);
        await vm.ApplyFiltersAsync();
        Assert.False(vm.HasFilterError);
        Assert.Equal(3, vm.Rows.Count);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("-1")]
    [InlineData("Infinity")]
    [InlineData("one")]
    public async Task InvalidHourTextDoesNotSilentlyDisableFilter(string value)
    {
        using var vm = new GamesViewModel(new Libraries());
        await vm.LoadAsync("A", false, default);
        vm.MinimumHours = value;
        await vm.ApplyFiltersAsync();
        Assert.True(vm.HasFilterError);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task RapidQueriesAndModeChangesApplyOnlyTheLatestFilter()
    {
        using var vm = new GamesViewModel(new Libraries
        {
            Owned = (_, _) => Task.FromResult(Outcome<OwnedLibrary>.Ready(new(
                Enumerable.Range(1, 20_000).Select(i => new LibraryGame((uint)i, $"Game {i}", i, null, null)).ToArray(), 20_000, true))),
            Recent = (_, _) => Task.FromResult(Outcome<RecentGames>.Ready(new([new(42, "Only recent", 123, 60, null)], 1)))
        });
        await vm.LoadAsync("A", false, default);
        vm.Query = "Game";
        var previous = vm.ApplyFiltersAsync();
        vm.ShowRecent = true;
        vm.Query = "Only recent";
        await vm.ApplyFiltersAsync();
        await previous;
        Assert.Equal(42u, Assert.Single(vm.Rows).AppId);
        Assert.False(vm.IsFiltering);
    }

    [Fact]
    public async Task SwitchingToRecentClearsTheOwnedOnlyNeverPlayedFilter()
    {
        using var vm = new GamesViewModel(new Libraries
        {
            Recent = (_, _) => Task.FromResult(Outcome<RecentGames>.Ready(new([new(42, "Recent", 60, 20, null)], 1)))
        });
        await vm.LoadAsync("A", false, default);
        vm.UnplayedOnly = true;
        await vm.ApplyFiltersAsync();
        Assert.Equal(440u, Assert.Single(vm.Rows).AppId);
        vm.ShowRecent = true;
        await vm.ApplyFiltersAsync();
        Assert.False(vm.UnplayedOnly);
        Assert.False(vm.HasFilterError);
        Assert.Equal(42u, Assert.Single(vm.Rows).AppId);
    }

    [Fact]
    public async Task PreservedFiltersDoNotHidePrivacyOrLoadingStates()
    {
        using var vm = new GamesViewModel(new Libraries
        {
            Owned = (id, _) => Task.FromResult(id == "A" ? Library(1) : Outcome<OwnedLibrary>.Failure(DataState.Private, "Private"))
        });
        await vm.LoadAsync("A", false, default);
        vm.UnplayedOnly = true;
        vm.MinimumHours = "invalid";
        await vm.LoadAsync("B", false, default);
        Assert.False(vm.HasFilterError);
        Assert.False(vm.UnplayedOnly);
        Assert.Contains("Access denied", vm.ActiveStateMessage);
    }

    [Fact]
    public async Task CancellationFinishesEvenIfServiceIgnoresTheToken()
    {
        using var cts = new CancellationTokenSource();
        using var vm = new GamesViewModel(new Libraries { Owned = (_, _) => Pending<OwnedLibrary>().Task, Recent = (_, _) => Pending<RecentGames>().Task });
        var load = vm.LoadAsync("A", false, cts.Token);
        cts.Cancel();
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Rows);
        Assert.Contains("cancelled", vm.OwnedStatus);
    }

    [Fact]
    public async Task AlreadyCancelledSearchCannotLeaveCompletedResponsesLoading()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var vm = new GamesViewModel(new Libraries());
        await vm.LoadAsync("A", false, cts.Token);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Rows);
        Assert.Contains("cancelled", vm.OwnedStatus);
        Assert.Contains("cancelled", vm.RecentStatus);
    }

    [Fact]
    public async Task ForceFlagAndIndependentTimestampsArePreserved()
    {
        var first = DateTimeOffset.UtcNow.AddMinutes(-3);
        var second = first.AddMinutes(1);
        var forced = new List<bool>();
        using var vm = new GamesViewModel(new Libraries
        {
            Owned = (_, _) => Task.FromResult(Library(1) with { UpdatedAt = first, FromCache = true }),
            Recent = (_, _) => Task.FromResult(new Outcome<RecentGames>(DataState.Empty, new([], 0), UpdatedAt: second)),
            OnForce = forced.Add
        });
        await vm.LoadAsync("A", true, default);
        Assert.Equal(new[] { true, true }, forced);
        Assert.Contains("cached", vm.OwnedUpdatedAt);
        Assert.DoesNotContain("cached", vm.RecentUpdatedAt);
        Assert.NotEqual(vm.OwnedUpdatedAt, vm.RecentUpdatedAt);
    }

    private static TaskCompletionSource<Outcome<T>> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Outcome<OwnedLibrary> Library(uint id) => Outcome<OwnedLibrary>.Ready(new([new(id, $"Game {id}", 60, null, null)], 1, true));
    private sealed class Libraries : ILibraryService
    {
        public Func<string, CancellationToken, Task<Outcome<OwnedLibrary>>> Owned { get; init; } = (_, _) => Task.FromResult(
            Outcome<OwnedLibrary>.Ready(new([new(730, "Counter-Strike 2", 120, 5, null), new(440, "Team Fortress 2", 0, 0, null), new(570, "Dota 2", 60, null, null)], 3, true)));
        public Func<string, CancellationToken, Task<Outcome<RecentGames>>> Recent { get; init; } = (_, _) => Task.FromResult(new Outcome<RecentGames>(DataState.Empty, new([], 0)));
        public Action<bool>? OnForce { get; init; }
        public Task<Outcome<OwnedLibrary>> GetLibraryAsync(string id, bool force, CancellationToken token) { OnForce?.Invoke(force); return Owned(id, token); }
        public Task<Outcome<RecentGames>> GetRecentAsync(string id, bool force, CancellationToken token) { OnForce?.Invoke(force); return Recent(id, token); }
    }
}
