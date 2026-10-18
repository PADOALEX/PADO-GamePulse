using Avalonia.Media.Imaging;
using GamePulse.Core;
using GamePulse.Desktop.Services;
using GamePulse.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class AnalysisExportTests
{
    private const string IdA = "76561198000000001", IdB = "76561198000000002", IdC = "76561198000000003";
    private static readonly DateTimeOffset Updated = new(2026, 10, 18, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Games_exports_only_current_filtered_rows_with_raw_values_and_source_identity()
    {
        using var fixture = new Fixture(new Libraries
        {
            Owned = (_, _) => Task.FromResult(Library(new LibraryGame(10, "Other", 0, 0, null), new(20, "Пульс, \"Game\"", null, 12, Updated)))
        });
        await fixture.OpenAsync(IdA);
        fixture.Model.ShowGamesCommand.Execute(null);
        fixture.Games.Query = "Пульс"; await fixture.Games.ApplyFiltersAsync();
        var table = Assert.IsType<ExportTable>(AnalysisExport.Capture(fixture.Model));
        Assert.Equal("games", table.Kind);
        Assert.Equal(new string?[] { "'" + IdA, "filtered_games", "complete_returned_library", DataExport.Timestamp(Updated),
            "20", "Пульс, \"Game\"", null, "12", DataExport.Timestamp(Updated) }, Assert.Single(table.Rows));
        Assert.Equal(table.Headers.Count, table.Rows[0].Count);
    }

    [Fact]
    public async Task Recent_export_uses_recent_snapshot_timestamp_and_never_claims_whole_library_coverage()
    {
        var recentTime = Updated.AddMinutes(5);
        using var fixture = new Fixture(new Libraries
        {
            Recent = (_, _) => Task.FromResult(Outcome<RecentGames>.Ready(new(
                [new(40, "Recent", 700, 5, Updated), new(50, "Excluded", 2, 2, null)], 2), recentTime))
        });
        await fixture.OpenAsync(IdA); fixture.Model.ShowGamesCommand.Execute(null);
        fixture.Games.ShowRecent = true; fixture.Games.Query = "Recent"; await fixture.Games.ApplyFiltersAsync();
        var table = Assert.IsType<ExportTable>(AnalysisExport.Capture(fixture.Model));
        Assert.Equal("recent-games", table.Kind);
        var row = Assert.Single(table.Rows);
        Assert.Equal("filtered_recent-games", row[1]); Assert.Equal("recent_activity_only", row[2]);
        Assert.Equal(DataExport.Timestamp(recentTime), row[3]); Assert.Equal("40", row[4]);
        Assert.Equal("700", row[6]); Assert.Equal("5", row[7]);
    }

    [Fact]
    public async Task Comparison_export_uses_both_independent_profiles_and_the_current_mutual_filter()
    {
        using var fixture = new Fixture(new Libraries
        {
            Owned = (id, _) => Task.FromResult(Library(new LibraryGame(10, "Keep", id == IdA ? 60 : 120, null, null),
                new(20, "Exclude", 0, null, null)) with { UpdatedAt = id == IdA ? Updated : Updated.AddMinutes(1) })
        });
        await fixture.OpenAsync(IdC); // The Explorer profile is intentionally unrelated to either side.
        fixture.Comparison.SideA.Input = IdA; fixture.Comparison.SideB.Input = IdB;
        await fixture.Comparison.CompareAsync(false); fixture.Model.ShowComparisonCommand.Execute(null);
        fixture.Comparison.Query = "Keep"; await fixture.Comparison.ApplyFiltersAsync();
        var table = Assert.IsType<ExportTable>(AnalysisExport.Capture(fixture.Model));
        Assert.Equal("mutual-games", table.Kind);
        Assert.Equal(new string?[] { "'" + IdA, "'" + IdB, "filtered_mutual_games", DataExport.Timestamp(Updated),
            DataExport.Timestamp(Updated.AddMinutes(1)), "10", "Keep", "60", "120" }, Assert.Single(table.Rows));
    }

    [Fact]
    public async Task Unknown_mutual_game_name_is_empty_in_csv_instead_of_the_display_appid_fallback()
    {
        using var fixture = new Fixture(new Libraries
        {
            Owned = (id, _) => Task.FromResult(Library(new LibraryGame(10, null, id == IdA ? 60 : 120, null, null)))
        });
        fixture.Comparison.SideA.Input = IdA; fixture.Comparison.SideB.Input = IdB;
        await fixture.Comparison.CompareAsync(false); fixture.Model.ShowComparisonCommand.Execute(null);
        await fixture.Comparison.ApplyFiltersAsync();
        Assert.Equal("10", Assert.Single(fixture.Comparison.MutualGames).Name);
        var table = Assert.IsType<ExportTable>(AnalysisExport.Capture(fixture.Model));
        Assert.Null(Assert.Single(table.Rows)[6]);
    }

    [Fact]
    public async Task Partial_inventory_export_keeps_coverage_quantities_identifiers_and_unknown_metadata()
    {
        using var fixture = new Fixture(inventory: new Inventories
        {
            Get = (request, _) => Task.FromResult(Outcome<InventorySnapshot>.Ready(new(request,
                [new("18446744073709551615", "200", "0", ulong.MaxValue, null, null, null, null, null, false),
                 new("1", "201", "0", 1, "Excluded", "Other", null, true, false, true)],
                900, false, 1, DataState.RateLimited), Updated))
        });
        await fixture.OpenAsync(IdA); fixture.Model.ShowInventoryCommand.Execute(null);
        fixture.Inventory.SelectedAppIndex = 5; await fixture.Inventory.LoadAsync(false);
        fixture.Inventory.Query = "18446744073709551615"; await fixture.Inventory.ApplyFiltersAsync();
        var table = Assert.IsType<ExportTable>(AnalysisExport.Capture(fixture.Model));
        Assert.Equal("inventory", table.Kind);
        Assert.Equal(new string?[] { "'" + IdA, "753", "'6", "filtered_inventory", "partial_returned_inventory",
            DataExport.Timestamp(Updated), "'18446744073709551615", "'200", "'0", null, null,
            "18446744073709551615", null, null, "false" }, Assert.Single(table.Rows));
    }

    [Fact]
    public async Task Captured_table_does_not_change_after_filters_profile_or_source_arrays_change()
    {
        LibraryGame[] source = [new(10, "Original", 60, null, null)];
        using var fixture = new Fixture(new Libraries
        { Owned = (_, _) => Task.FromResult(Outcome<OwnedLibrary>.Ready(new(source, 1, true), Updated)) });
        await fixture.OpenAsync(IdA); fixture.Model.ShowGamesCommand.Execute(null);
        var table = Assert.IsType<ExportTable>(AnalysisExport.Capture(fixture.Model));
        var original = table.Rows[0].ToArray();
        source[0] = new(20, "Changed", 999, null, null);
        fixture.Games.Query = "No match"; await fixture.Games.ApplyFiltersAsync();
        await fixture.OpenAsync(IdB);
        Assert.Equal(original, Assert.Single(table.Rows));
        Assert.Equal("'" + IdA, table.Rows[0][0]); Assert.Equal("Original", table.Rows[0][5]);
    }

    [Theory]
    [InlineData(DataState.Private)]
    [InlineData(DataState.Unavailable)]
    [InlineData(DataState.Failed)]
    public async Task Inaccessible_sources_do_not_export_empty_or_invented_data(DataState state)
    {
        using var fixture = new Fixture(new Libraries
        { Owned = (_, _) => Task.FromResult(Outcome<OwnedLibrary>.Failure(state, "Unavailable")) }, new Inventories
        { Get = (_, _) => Task.FromResult(Outcome<InventorySnapshot>.Failure(state, "Unavailable")) });
        await fixture.OpenAsync(IdA); fixture.Model.ShowGamesCommand.Execute(null);
        Assert.Null(AnalysisExport.Capture(fixture.Model));
        fixture.Comparison.SideA.Input = IdA; fixture.Comparison.SideB.Input = IdB;
        await fixture.Comparison.CompareAsync(false); fixture.Model.ShowComparisonCommand.Execute(null);
        Assert.Null(AnalysisExport.Capture(fixture.Model));
        await fixture.Inventory.LoadAsync(false); fixture.Model.ShowInventoryCommand.Execute(null);
        Assert.Null(AnalysisExport.Capture(fixture.Model));
    }

    [Fact]
    public async Task Loading_game_data_is_not_exportable_until_the_snapshot_is_ready()
    {
        var pending = new TaskCompletionSource<Outcome<OwnedLibrary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(new Libraries { Owned = (_, _) => pending.Task });
        var load = fixture.OpenAsync(IdA); fixture.Model.ShowGamesCommand.Execute(null);
        Assert.True(fixture.Games.ActiveLoading); Assert.Null(AnalysisExport.Capture(fixture.Model));
        pending.SetResult(Library(new LibraryGame(10, "Ready", 1, null, null))); await load;
        Assert.NotNull(AnalysisExport.Capture(fixture.Model));
    }

    [Fact]
    public async Task Loading_inventory_is_not_exportable_until_the_snapshot_is_ready()
    {
        var pending = new TaskCompletionSource<Outcome<InventorySnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(inventory: new Inventories { Get = (_, _) => pending.Task });
        await fixture.OpenAsync(IdA); fixture.Model.ShowInventoryCommand.Execute(null);
        var load = fixture.Inventory.LoadAsync(false);
        Assert.True(fixture.Inventory.IsBusy); Assert.Null(AnalysisExport.Capture(fixture.Model));
        pending.SetResult(Inventory(new(IdA, 730, "2"))); await load;
        Assert.NotNull(AnalysisExport.Capture(fixture.Model));
    }

    [Fact]
    public async Task Pending_filters_invalid_filters_empty_matches_and_unsupported_pages_do_not_export()
    {
        using var fixture = new Fixture(); await fixture.OpenAsync(IdA);
        fixture.Model.ShowGamesCommand.Execute(null); Assert.NotNull(AnalysisExport.Capture(fixture.Model));
        fixture.Games.Query = "No matching game";
        Assert.True(fixture.Games.IsFiltering); Assert.Null(AnalysisExport.Capture(fixture.Model));
        await fixture.Games.ApplyFiltersAsync(); Assert.Null(AnalysisExport.Capture(fixture.Model));
        fixture.Games.Query = ""; fixture.Games.MinimumHours = "invalid"; await fixture.Games.ApplyFiltersAsync();
        Assert.True(fixture.Games.HasFilterError); Assert.Null(AnalysisExport.Capture(fixture.Model));
        fixture.Games.MinimumHours = ""; await fixture.Games.ApplyFiltersAsync();
        foreach (var command in new[] { fixture.Model.ShowOverviewCommand, fixture.Model.ShowFriendsCommand,
            fixture.Model.ShowAchievementsCommand, fixture.Model.ShowInsightsCommand })
        { command.Execute(null); Assert.Null(AnalysisExport.Capture(fixture.Model)); }
    }

    private static Outcome<OwnedLibrary> Library(params LibraryGame[] games) =>
        Outcome<OwnedLibrary>.Ready(new(games, games.Length, true), Updated);
    private static Outcome<InventorySnapshot> Inventory(InventoryRequest request) => Outcome<InventorySnapshot>.Ready(new(request,
        [new("1", "2", "0", 1, "Item", "Type", null, true, false, true)], 1, true, 1), Updated);
    private sealed class Fixture : IDisposable
    {
        private readonly AvatarService avatars = new();
        public GamesViewModel Games { get; }
        public ComparisonViewModel Comparison { get; }
        public InventoryViewModel Inventory { get; }
        public MainWindowViewModel Model { get; }
        public Fixture(Libraries? libraries = null, Inventories? inventory = null)
        {
            libraries ??= new(); var profiles = new Profiles();
            Games = new(libraries); Comparison = new(profiles, libraries, new ProfileComparisonService());
            Inventory = new(inventory ?? new(), new Icons());
            Model = new(profiles, avatars, NullLogger<MainWindowViewModel>.Instance, games: Games,
                comparison: Comparison, inventory: Inventory, apiKeyConfigured: true);
        }
        public Task OpenAsync(string id) { Model.SearchInput = id; return Model.SearchAsync(false); }
        public void Dispose() { Model.Dispose(); avatars.Dispose(); }
    }
    private sealed class Profiles : IProfileLookupService
    {
        public Task<Outcome<SteamProfile>> FindAsync(string input, bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(Outcome<SteamProfile>.Ready(new(input, "Fixture", "https://steamcommunity.com/profiles/" + input,
                null, 3, 1, null, null, null), Updated));
        public Task<Outcome<ProfileLevel>> GetLevelAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(Outcome<ProfileLevel>.Ready(new(1), Updated));
        public Task<Outcome<SteamBans>> GetBansAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(Outcome<SteamBans>.Ready(new(null, null, null, null, null, null), Updated));
    }
    private sealed class Libraries : ILibraryService
    {
        public Func<string, CancellationToken, Task<Outcome<OwnedLibrary>>> Owned { get; init; } = (_, _) => Task.FromResult(Library(new LibraryGame(10, "Game", 60, null, null)));
        public Func<string, CancellationToken, Task<Outcome<RecentGames>>> Recent { get; init; } = (_, _) =>
            Task.FromResult(new Outcome<RecentGames>(DataState.Empty, new([], 0), UpdatedAt: Updated));
        public Task<Outcome<OwnedLibrary>> GetLibraryAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => Owned(steamId, cancellationToken);
        public Task<Outcome<RecentGames>> GetRecentAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => Recent(steamId, cancellationToken);
    }
    private sealed class Inventories : IInventoryService
    {
        public Func<InventoryRequest, CancellationToken, Task<Outcome<InventorySnapshot>>> Get { get; init; } = (request, _) => Task.FromResult(Inventory(request));
        public Task<Outcome<InventorySnapshot>> GetInventoryAsync(InventoryRequest request, bool forceRefresh,
            IProgress<InventoryProgress>? progress, CancellationToken cancellationToken) => Get(request, cancellationToken);
    }
    private sealed class Icons : IInventoryIconLoader
    { public Task<Bitmap?> LoadAsync(string? url, CancellationToken cancellationToken) => Task.FromResult<Bitmap?>(null); }
}
