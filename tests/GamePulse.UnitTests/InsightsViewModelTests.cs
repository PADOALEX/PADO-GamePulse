using GamePulse.Core;
using GamePulse.Desktop.ViewModels;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class InsightsViewModelTests
{
    [Fact]
    public async Task Reacts_to_loaded_library_without_extra_requests_or_game_filtering_effects()
    {
        var service = new Libraries();
        using var games = new GamesViewModel(service);
        using var insights = new InsightsViewModel(games);
        Assert.False(insights.HasData);
        Assert.Equal("N/A", insights.TotalHours);
        await games.LoadAsync("A", false, default);
        await insights.RefreshAsync();
        Assert.True(insights.HasData);
        Assert.Equal(DataState.Ready, insights.State);
        Assert.Equal(3, insights.TopGames.Count);
        Assert.Equal("1", insights.UnplayedCount);
        var total = insights.TotalHours;
        games.Query = "never matches";
        await games.ApplyFiltersAsync();
        games.ShowRecent = true;
        await games.ApplyFiltersAsync();
        await insights.RefreshAsync();
        Assert.Equal(total, insights.TotalHours);
        Assert.Equal(3, insights.TopGames.Count);
        Assert.Equal(1, service.OwnedRequests);
        Assert.Equal(1, service.RecentRequests);
    }

    [Theory]
    [InlineData(DataState.Private)]
    [InlineData(DataState.Unavailable)]
    [InlineData(DataState.MissingKey)]
    [InlineData(DataState.RateLimited)]
    [InlineData(DataState.Failed)]
    public async Task Unavailable_data_does_not_become_zero_or_retain_previous_profile(DataState state)
    {
        var service = new Libraries();
        using var games = new GamesViewModel(service);
        using var insights = new InsightsViewModel(games);
        await games.LoadAsync("A", false, default);
        await insights.RefreshAsync();
        insights.SelectedGame = insights.TopGames[0];
        service.Next = Outcome<OwnedLibrary>.Failure(state, "Static safe description");
        await games.LoadAsync("B", false, default);
        await insights.RefreshAsync();
        Assert.False(insights.HasData);
        Assert.Equal(state, insights.State);
        Assert.Empty(insights.TopGames);
        Assert.Empty(insights.Buckets);
        Assert.Null(insights.SelectedGame);
        Assert.Equal("N/A", insights.TotalHours);
        Assert.Equal("N/A", insights.UnplayedCount);
        Assert.Equal("N/A", insights.TopThreeShare);
        Assert.DoesNotContain("empty", insights.StatusMessage);
    }

    [Fact]
    public async Task Loading_and_clear_remove_stale_metrics_immediately()
    {
        var service = new Libraries();
        using var games = new GamesViewModel(service);
        using var insights = new InsightsViewModel(games);
        await games.LoadAsync("A", false, default);
        await insights.RefreshAsync();
        var pending = new TaskCompletionSource<Outcome<OwnedLibrary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Pending = pending.Task;
        var load = games.LoadAsync("B", false, default);
        Assert.True(insights.IsLoading);
        Assert.False(insights.HasData);
        Assert.Empty(insights.TopGames);
        Assert.Equal("N/A", insights.TotalHours);
        games.Clear();
        pending.SetResult(service.Next);
        await load;
        await insights.RefreshAsync();
        Assert.False(insights.HasData);
        Assert.False(insights.IsLoading);
        Assert.Equal("N/A", insights.TotalHours);
    }

    [Fact]
    public async Task TranslationRefreshRetainsSourceRowsAndSelectedGameAndNotifiesEveryRow()
    {
        var service = new Libraries();
        using var games = new GamesViewModel(service);
        using var insights = new InsightsViewModel(games);
        await games.LoadAsync("A", false, default);
        await insights.RefreshAsync();
        insights.SelectedGame = insights.TopGames[0];
        var selected = insights.SelectedGame;
        var topGames = insights.TopGames;
        var buckets = insights.Buckets;
        var changedGames = new HashSet<InsightGameRow>();
        var changedBuckets = new HashSet<InsightBucketRow>();
        foreach (var row in topGames) row.PropertyChanged += (_, _) => changedGames.Add(row);
        foreach (var row in buckets) row.PropertyChanged += (_, _) => changedBuckets.Add(row);

        insights.RefreshLocalization();

        Assert.Same(topGames, insights.TopGames);
        Assert.Same(buckets, insights.Buckets);
        Assert.Same(selected, insights.SelectedGame);
        Assert.Equal(topGames.Count, changedGames.Count);
        Assert.Equal(buckets.Count, changedBuckets.Count);
        Assert.Equal(1, service.OwnedRequests);
    }

    [Fact]
    public async Task Partial_library_shows_coverage_and_observed_buckets_but_no_whole_library_claims()
    {
        using var games = new GamesViewModel(new Libraries
        {
            Next = Outcome<OwnedLibrary>.Ready(new([new(1, "Visible", 0, null, null)], 10, false))
        });
        using var insights = new InsightsViewModel(games);
        await games.LoadAsync("A", false, default);
        await insights.RefreshAsync();
        Assert.True(insights.HasData);
        Assert.Contains("partial", insights.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 / 10", insights.AvailableCount);
        Assert.Equal("N/A", insights.UnplayedCount);
        Assert.Equal("1", insights.Buckets.Single(bucket => bucket.Bucket.Range == PlaytimeRange.Unplayed).Count);
    }

    [Fact]
    public async Task Truly_empty_library_keeps_explicit_empty_state_and_known_zero_totals()
    {
        using var games = new GamesViewModel(new Libraries { Next = new(DataState.Empty, new([], 0, true)) });
        using var insights = new InsightsViewModel(games);
        await games.LoadAsync("A", false, default);
        await insights.RefreshAsync();
        Assert.True(insights.HasData);
        Assert.Equal(DataState.Empty, insights.State);
        Assert.Contains("empty", insights.StatusMessage);
        Assert.Equal("0", insights.UnplayedCount);
        Assert.DoesNotContain("N/A", insights.TotalHours);
        Assert.Equal("N/A", insights.TopThreeShare);
    }

    private sealed class Libraries : ILibraryService
    {
        public int OwnedRequests { get; private set; }
        public int RecentRequests { get; private set; }
        public Task<Outcome<OwnedLibrary>>? Pending { get; set; }
        public Outcome<OwnedLibrary> Next { get; set; } = Outcome<OwnedLibrary>.Ready(new(
            [new(1, "First", 120, null, null), new(2, "Second", 60, null, null), new(3, "Unplayed", 0, null, null)], 3, true));
        public Task<Outcome<OwnedLibrary>> GetLibraryAsync(string id, bool force, CancellationToken token)
        {
            OwnedRequests++;
            return Pending ?? Task.FromResult(Next);
        }
        public Task<Outcome<RecentGames>> GetRecentAsync(string id, bool force, CancellationToken token)
        {
            RecentRequests++;
            return Task.FromResult(Outcome<RecentGames>.Failure(DataState.Private, "Private recent activity"));
        }
    }
}
