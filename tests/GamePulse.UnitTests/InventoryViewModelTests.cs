using System.Globalization;
using Avalonia.Media.Imaging;
using GamePulse.Core;
using GamePulse.Desktop.Resources;
using GamePulse.Desktop.Services;
using GamePulse.Desktop.ViewModels;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class InventoryViewModelTests
{
    private const string IdA = "76561198000000001", IdB = "76561198000000002";

    [Fact]
    public async Task ProfileSourceAndFilterChangesNeverFetchInventory()
    {
        var service = new Inventory(); using var vm = Create(service);
        Assert.False(vm.CanLoad); await vm.LoadAsync(false); Assert.Empty(service.Calls);
        vm.SetProfile(IdA); Assert.True(vm.CanLoad); Assert.Empty(vm.Rows); Assert.Contains("Load inventory", vm.StateMessage);
        vm.SelectedAppIndex = 5; Assert.Equal("6", vm.ContextId);
        vm.ContextId = "7"; vm.Query = "Item"; vm.MarketableOnly = true; await vm.ApplyFiltersAsync();
        Assert.Empty(service.Calls); Assert.Null(vm.SelectedItem); Assert.Equal("N/A", vm.TotalAssetCount);
        Assert.Equal(6, vm.Apps.Count);
    }

    [Fact]
    public async Task ExplicitLoadUsesCurrentProfileAppContextAndForceIsForwarded()
    {
        var service = new Inventory(); using var vm = Create(service); vm.SetProfile(IdA);
        vm.SelectedAppIndex = 5; vm.ContextId = "7"; await vm.LoadAsync(false);
        Assert.Equal(new InventoryRequest(IdA, 753, "7"), service.Calls[0].Request); Assert.False(service.Calls[0].Force);
        Assert.Equal(3, vm.Rows.Count); Assert.True(vm.IsComplete); Assert.Equal("3", vm.TotalAssetCount);
        await vm.LoadAsync(true); Assert.True(service.Calls[1].Force);
        Assert.Contains("753", vm.Source); Assert.Contains(IdA, vm.Source); Assert.Contains("7", vm.Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("02")]
    [InlineData(" 2 ")]
    [InlineData("../2")]
    [InlineData("18446744073709551616")]
    [InlineData("https://example.test")]
    public async Task InvalidContextProducesFriendlyValidationAndNoHttp(string context)
    {
        var service = new Inventory(); using var vm = Create(service); vm.SetProfile(IdA); vm.ContextId = context;
        Assert.False(vm.CanLoad); await vm.LoadAsync(false); Assert.Empty(service.Calls);
        Assert.Contains("numeric context ID", vm.StateMessage); Assert.Equal("N/A", vm.LoadedAssetCount);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("https://steamcommunity.com/profiles/76561198000000001/")]
    [InlineData(" 76561198000000001")]
    [InlineData("0")]
    public async Task SetProfileRequiresExactResolvedNumericSteamId(string input)
    {
        var service = new Inventory(); using var vm = Create(service); vm.SetProfile(input); await vm.LoadAsync(false);
        Assert.False(vm.HasProfile); Assert.False(vm.CanLoad); Assert.Empty(service.Calls);
        Assert.Contains("Open a profile", vm.StateMessage);
    }

    [Theory]
    [InlineData(DataState.Private, "private")]
    [InlineData(DataState.Unavailable, "unavailable")]
    [InlineData(DataState.RateLimited, "limiting")]
    [InlineData(DataState.Failed, "could not be loaded")]
    [InlineData(DataState.MissingKey, "API key")]
    public async Task AvailabilityStatesAreDistinctNeverZeroAndNeverDisplayRawErrors(DataState state, string message)
    {
        using var vm = Create(new Inventory { Get = (_, _, _, _) => Task.FromResult(Outcome<InventorySnapshot>.Failure(state, "Secret raw transport response")) });
        vm.SetProfile(IdA); await vm.LoadAsync(false);
        Assert.Equal(state, vm.State); Assert.Contains(message, vm.StateMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", vm.StateMessage); Assert.Empty(vm.Rows); Assert.False(vm.HasSnapshot);
        Assert.Equal("N/A", vm.TotalAssetCount); Assert.Equal("N/A", vm.LoadedAssetCount); Assert.Equal("N/A", vm.QuantityCount);
    }

    [Fact]
    public async Task ExplicitEmptyIsRealZeroAndIsNotUnavailable()
    {
        using var vm = Create(new Inventory { Get = (request, _, _, _) => Task.FromResult(new Outcome<InventorySnapshot>(DataState.Empty,
            new(request, [], 0, true, 1), null, DateTimeOffset.UtcNow)) });
        vm.SetProfile(IdA); await vm.LoadAsync(false);
        Assert.Equal(DataState.Empty, vm.State); Assert.True(vm.IsComplete); Assert.True(vm.HasSnapshot);
        Assert.Equal("0", vm.TotalAssetCount); Assert.Equal("0", vm.QuantityCount); Assert.Contains("empty inventory", vm.StateMessage);
    }

    [Theory]
    [InlineData(DataState.RateLimited, "limited")]
    [InlineData(DataState.Unavailable, "unavailable")]
    [InlineData(DataState.Failed, "could not be loaded")]
    public async Task PartialPagesKeepAllLoadedRowsWithQuantitiesButNoGlobalTotals(DataState interruption, string reason)
    {
        using var vm = Create(new Inventory { Get = (request, _, _, _) => Task.FromResult(Outcome<InventorySnapshot>.Ready(
            new(request, Items(), 900, false, 2, interruption))) });
        vm.SetProfile(IdA); await vm.LoadAsync(false);
        Assert.Equal(3, vm.Rows.Count); Assert.Equal("3", vm.LoadedAssetCount); Assert.Equal("9", vm.QuantityCount);
        Assert.Equal("N/A", vm.TotalAssetCount); Assert.True(vm.HasCompleteness); Assert.False(vm.IsComplete);
        Assert.Contains(reason, vm.Completeness); Assert.Contains("Incomplete inventory", vm.Completeness);
        Assert.Equal(InventoryText.Format("Coverage", 2, 3), vm.MetadataCoverage);
    }

    [Theory]
    [InlineData(InventoryStopReason.PageLimit, "local page safety limit")]
    [InlineData(InventoryStopReason.SizeLimit, "local size safety limit")]
    [InlineData(InventoryStopReason.SourceChanged, "inconsistent inventory counts")]
    [InlineData(InventoryStopReason.InvalidPagination, "inconsistent pagination")]
    [InlineData(InventoryStopReason.MissingTotal, "did not supply an inventory total")]
    public async Task TypedStopReasonExplainsPartialResultWithoutShowingRawTransportDetails(InventoryStopReason stopReason, string explanation)
    {
        using var vm = Create(new Inventory { Get = (request, _, _, _) => Task.FromResult(new Outcome<InventorySnapshot>(
            DataState.Ready, new(request, Items(), null, false, 1, DataState.Failed, stopReason),
            "Unsafe https://example.test/?key=NeverShowThisSecret")) });
        vm.SetProfile(IdA); await vm.LoadAsync(false);
        Assert.Contains(explanation, vm.Completeness); Assert.Contains("Global totals are N/A", vm.Completeness);
        Assert.DoesNotContain("https", vm.Completeness); Assert.DoesNotContain("NeverShow", vm.Completeness);
        Assert.Equal("N/A", vm.TotalAssetCount); Assert.Equal(3, vm.Rows.Count);
    }

    [Fact]
    public async Task MissingDescriptionHasIdFallbackAndUnknownFlagsInsteadOfNo()
    {
        using var vm = Create(); vm.SetProfile(IdA); await vm.LoadAsync(false);
        var row = vm.Rows.Single(x => !x.Item.HasDescription);
        Assert.Equal("Asset 103", row.Name); Assert.Equal("Unknown type", row.Type);
        Assert.Equal("Unknown", row.Marketable); Assert.Equal("Unknown", row.Tradable); Assert.False(row.IsMarketable); Assert.False(row.IsTradable);
        vm.SelectedItem = row; await vm.IconLoadingTask; Assert.Contains("matching item description", vm.SelectedItem!.Metadata);
        vm.MarketableOnly = true; await vm.ApplyFiltersAsync(); Assert.Equal("101", Assert.Single(vm.Rows).AssetId); Assert.Null(vm.SelectedItem);
        vm.MarketableOnly = false; vm.TradableOnly = true; await vm.ApplyFiltersAsync(); Assert.Equal("102", Assert.Single(vm.Rows).AssetId);
    }

    [Theory]
    [InlineData("A polished", "101")]
    [InlineData("Pistol", "101")]
    [InlineData("102", "102")]
    [InlineData("202", "102")]
    public async Task SearchCoversNamesTypesAndNumericIdentifiers(string query, string id)
    {
        using var vm = Create(); vm.SetProfile(IdA); await vm.LoadAsync(false);
        vm.Query = query; await vm.ApplyFiltersAsync(); Assert.Equal(id, Assert.Single(vm.Rows).AssetId);
    }

    [Fact]
    public async Task SortAmountAndResetApplyToEntireSource()
    {
        using var vm = Create(); vm.SetProfile(IdA); await vm.LoadAsync(false);
        vm.SelectedSortIndex = 2; await vm.ApplyFiltersAsync(); Assert.Equal("102", vm.Rows[0].AssetId);
        vm.Query = "missing"; await vm.ApplyFiltersAsync(); Assert.Empty(vm.Rows); Assert.Contains("No items match", vm.StateMessage);
        vm.MarketableOnly = vm.TradableOnly = true; vm.ResetFiltersCommand.Execute(null); await vm.FilteringTask;
        Assert.Equal(3, vm.Rows.Count); Assert.Equal(0, vm.SelectedSortIndex); Assert.False(vm.MarketableOnly); Assert.False(vm.TradableOnly);
        Assert.Equal("", vm.Query); Assert.Equal("101", vm.Rows[0].AssetId);
    }

    [Fact]
    public async Task LargeSourceHasNoHiddenRowCapAndLastItemRemainsSearchable()
    {
        var items = Enumerable.Range(1, 12000).Select(i => new InventoryItem(i.ToString(), (20000 + i).ToString(), "0", 1,
            $"Item {i:D5}", "Test type", null, true, true, true)).ToArray();
        using var vm = Create(new Inventory { Get = (request, _, _, _) => Task.FromResult(Outcome<InventorySnapshot>.Ready(new(request, items, 12000, true, 6))) });
        vm.SetProfile(IdA); await vm.LoadAsync(false); Assert.Equal(12000, vm.Rows.Count);
        vm.Query = "Item 12000"; await vm.ApplyFiltersAsync(); Assert.Equal("12000", Assert.Single(vm.Rows).AssetId);
        Assert.Equal(12000.ToString("N0", CultureInfo.CurrentCulture), vm.TotalAssetCount);
    }

    [Fact]
    public async Task ProfileChangeCancelsNonCooperativeLoadAndIgnoresLateResult()
    {
        var pending = Pending<Outcome<InventorySnapshot>>(); CancellationToken oldToken = default;
        using var vm = Create(new Inventory { Get = (request, _, _, token) => { oldToken = token; return pending.Task; } });
        vm.SetProfile(IdA); var old = vm.LoadAsync(false); Assert.True(vm.IsBusy);
        vm.SetProfile(IdB); await old.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(oldToken.IsCancellationRequested);
        pending.SetResult(Ready(new(IdA, 730, "2"))); Assert.Equal(IdB, vm.SteamId); Assert.Empty(vm.Rows);
        Assert.False(vm.IsBusy); Assert.Contains("Load inventory", vm.StateMessage);
    }

    [Fact]
    public async Task SameProfileDoesNotDiscardCurrentInventory()
    {
        var service = new Inventory(); using var vm = Create(service); vm.SetProfile(IdA); await vm.LoadAsync(false);
        var rows = vm.Rows; vm.SetProfile(IdA); Assert.Same(rows, vm.Rows); Assert.Single(service.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AppAndContextChangeCancelOldLoadClearRowsAndDoNotAutomaticallyReload(bool changeApp)
    {
        var pending = Pending<Outcome<InventorySnapshot>>(); var service = new Inventory { Get = (_, _, _, _) => pending.Task };
        using var vm = Create(service); vm.SetProfile(IdA); var old = vm.LoadAsync(false);
        if (changeApp) vm.SelectedAppIndex = 5; else vm.ContextId = "9";
        await old.WaitAsync(TimeSpan.FromSeconds(3)); pending.SetResult(Ready(new(IdA, 730, "2")));
        Assert.Single(service.Calls); Assert.Empty(vm.Rows); Assert.Null(vm.SelectedItem); Assert.False(vm.IsBusy);
        Assert.Equal("N/A", vm.QuantityCount); Assert.True(vm.CanLoad);
    }

    [Fact]
    public async Task CancelCompletesPromptlyAndDoesNotPublishLateResult()
    {
        var pending = Pending<Outcome<InventorySnapshot>>(); CancellationToken captured = default;
        using var vm = Create(new Inventory { Get = (_, _, _, token) => { captured = token; return pending.Task; } });
        vm.SetProfile(IdA); var load = vm.LoadAsync(false); vm.Cancel(); await load.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(captured.IsCancellationRequested); Assert.False(vm.IsBusy); Assert.Contains("cancelled", vm.StateMessage);
        pending.SetResult(Ready(new(IdA, 730, "2"))); Assert.Empty(vm.Rows); Assert.Equal("N/A", vm.QuantityCount);
    }

    [Fact]
    public async Task RefreshClearsOldDataImmediatelyAndFailureDoesNotRestoreIt()
    {
        var pending = Pending<Outcome<InventorySnapshot>>(); var refresh = false;
        using var vm = Create(new Inventory { Get = (request, _, _, _) => refresh ? pending.Task : Task.FromResult(Ready(request)) });
        vm.SetProfile(IdA); await vm.LoadAsync(false); vm.SelectedItem = vm.Rows[0]; Assert.Equal(3, vm.Rows.Count);
        refresh = true; var loading = vm.LoadAsync(true); Assert.Empty(vm.Rows); Assert.Null(vm.SelectedItem); Assert.Equal("N/A", vm.TotalAssetCount);
        pending.SetResult(Outcome<InventorySnapshot>.Failure(DataState.Failed, "Secret")); await loading;
        Assert.Equal(DataState.Failed, vm.State); Assert.Empty(vm.Rows); Assert.Equal("N/A", vm.QuantityCount);
    }

    [Fact]
    public async Task NewerExplicitLoadWinsOverOlderNonCooperativeLoad()
    {
        var pending = Pending<Outcome<InventorySnapshot>>(); var calls = 0;
        using var vm = Create(new Inventory { Get = (request, _, _, _) => ++calls == 1 ? pending.Task : Task.FromResult(Ready(request)) });
        vm.SetProfile(IdA); var old = vm.LoadAsync(false); await vm.LoadAsync(true); await old;
        pending.SetResult(Outcome<InventorySnapshot>.Failure(DataState.Unavailable, "Old failed"));
        Assert.Equal(DataState.Ready, vm.State); Assert.Equal(3, vm.Rows.Count);
    }

    [Fact]
    public async Task AResponseForAnotherSourceIsRejected()
    {
        using var vm = Create(new Inventory { Get = (_, _, _, _) => Task.FromResult(Ready(new(IdB, 440, "2"))) });
        vm.SetProfile(IdA); await vm.LoadAsync(false); Assert.Equal(DataState.Failed, vm.State);
        Assert.Empty(vm.Rows); Assert.Equal("N/A", vm.TotalAssetCount);
    }

    [Fact]
    public async Task TransportExceptionIsSafeAndDoesNotCrash()
    {
        using var vm = Create(new Inventory { Get = (_, _, _, _) => throw new HttpRequestException("Secret untrusted transport details") });
        vm.SetProfile(IdA); await vm.LoadAsync(false); Assert.Equal(DataState.Failed, vm.State);
        Assert.DoesNotContain("Secret", vm.StateMessage); Assert.True(vm.CanLoad);
    }

    [Fact]
    public async Task FilterDebouncePreservesNewerSelectionAndDoesNotLoadIconAgain()
    {
        var icons = new Icons(); using var vm = Create(icons: icons); vm.SetProfile(IdA); await vm.LoadAsync(false);
        vm.SelectedItem = vm.Rows[0]; await vm.IconLoadingTask;
        vm.Query = ""; vm.SelectedSortIndex = 2; var filtering = vm.FilteringTask;
        vm.SelectedItem = vm.Rows.Single(x => x.AssetId == "102"); await vm.IconLoadingTask;
        await filtering; await vm.IconLoadingTask;
        Assert.Equal("102", vm.SelectedItem?.AssetId); Assert.Equal(2, icons.Calls);
    }

    [Fact]
    public async Task DirectoryAndQueryNeverLoadIconsUntilSelection()
    {
        var icons = new Icons(); using var vm = Create(icons: icons); vm.SetProfile(IdA); await vm.LoadAsync(false);
        await vm.ApplyFiltersAsync(); Assert.Equal(0, icons.Calls); vm.SelectedItem = vm.Rows[0]; await vm.IconLoadingTask;
        Assert.Equal(1, icons.Calls); var selected = vm.SelectedItem; vm.SelectedItem = selected; await vm.ApplyFiltersAsync();
        await vm.IconLoadingTask; Assert.Equal(1, icons.Calls);
        vm.Query = "B unusual"; await vm.ApplyFiltersAsync(); Assert.False(vm.HasSelection); Assert.Null(vm.SelectedIcon);
    }

    [Fact]
    public async Task ChangingSelectionCancelsAndSuppressesOlderIcon()
    {
        var pending = Pending<Bitmap?>(); CancellationToken oldToken = default;
        var icons = new Icons { Get = (url, token) => url!.Contains("101", StringComparison.Ordinal) ? Capture(token) : Task.FromResult<Bitmap?>(null) };
        Task<Bitmap?> Capture(CancellationToken token) { oldToken = token; return pending.Task; }
        using var vm = Create(icons: icons); vm.SetProfile(IdA); await vm.LoadAsync(false);
        vm.SelectedItem = vm.Rows.Single(x => x.AssetId == "101"); var old = vm.IconLoadingTask;
        vm.SelectedItem = vm.Rows.Single(x => x.AssetId == "102"); await vm.IconLoadingTask;
        Assert.True(oldToken.IsCancellationRequested); pending.SetResult(null); await old;
        Assert.Equal("102", vm.SelectedItem?.AssetId); Assert.Null(vm.SelectedIcon);
    }

    [Fact]
    public async Task LocalizationRefreshNotifiesRetainedItemDetailsWithoutReloadingIconOrInventory()
    {
        var service = new Inventory();
        var icons = new Icons();
        using var vm = Create(service, icons);
        vm.SetProfile(IdA);
        await vm.LoadAsync(false);
        vm.SelectedItem = vm.Rows.Single(row => row.AssetId == "101");
        await vm.IconLoadingTask;
        var selected = vm.SelectedItem!;
        var rowNotifications = 0;
        selected.PropertyChanged += (_, _) => rowNotifications++;
        await vm.ApplyFiltersAsync();
        Assert.Same(selected, vm.SelectedItem);
        Assert.DoesNotContain(vm.Rows, row => ReferenceEquals(row, selected));
        var rowSource = vm.Rows;
        var imageRequests = icons.Calls;
        var selectedPropertyNotified = false;
        vm.PropertyChanged += (_, args) => selectedPropertyNotified |= args.PropertyName == nameof(vm.SelectedItem);

        vm.RefreshLocalization();

        Assert.True(rowNotifications > 0);
        Assert.True(selectedPropertyNotified);
        Assert.Same(selected, vm.SelectedItem);
        Assert.Same(rowSource, vm.Rows);
        Assert.Equal(imageRequests, icons.Calls);
        Assert.Single(service.Calls);
    }

    [Fact]
    public async Task IconExceptionDoesNotHideItemFacts()
    {
        using var vm = Create(icons: new Icons { Get = (_, _) => throw new HttpRequestException("Do not expose") });
        vm.SetProfile(IdA); await vm.LoadAsync(false); vm.SelectedItem = vm.Rows[0]; await vm.IconLoadingTask;
        Assert.True(vm.HasSelection); Assert.Null(vm.SelectedIcon); Assert.Equal(DataState.Ready, vm.State); Assert.Equal("", vm.LocalMessage);
    }

    [Fact]
    public async Task ClearingSourceCancelsPendingFiltersAndDropsSelectionWithoutNewIconRequest()
    {
        var icons = new Icons(); using var vm = Create(icons: icons); vm.SetProfile(IdA); await vm.LoadAsync(false);
        vm.SelectedItem = vm.Rows[0]; await vm.IconLoadingTask; vm.Query = "polished"; var filtering = vm.FilteringTask;
        vm.Clear(); await filtering; Assert.Empty(vm.Rows); Assert.Null(vm.SelectedItem); Assert.Null(vm.SelectedIcon);
        Assert.False(vm.IsFiltering); Assert.False(vm.HasProfile); Assert.Equal(1, icons.Calls);
    }

    [Fact]
    public async Task QuantitySumDoesNotOverflowUlong()
    {
        var items = Items().Take(2).Select(x => x with { Amount = ulong.MaxValue }).ToArray();
        using var vm = Create(new Inventory { Get = (request, _, _, _) => Task.FromResult(Outcome<InventorySnapshot>.Ready(new(request, items, 2, true, 1))) });
        vm.SetProfile(IdA); await vm.LoadAsync(false);
        Assert.Equal((2m * ulong.MaxValue).ToString("N0", CultureInfo.CurrentCulture), vm.QuantityCount);
    }

    [Fact]
    public async Task CachedTimestampIsExplicitAndForceStateRemainsSafe()
    {
        using var vm = Create(new Inventory { Get = (request, _, _, _) => Task.FromResult(Ready(request) with { FromCache = true }) });
        vm.SetProfile(IdA); await vm.LoadAsync(false); Assert.Contains("Cached", vm.UpdatedAt);
    }

    [Fact]
    public async Task PageProgressIsVisibleOnlyForTheCurrentSourceAndPendingRequest()
    {
        var pending = Pending<Outcome<InventorySnapshot>>();
        var reports = new List<IProgress<InventoryProgress>>();
        using var vm = Create(new Inventory { Get = (_, _, progress, _) => { reports.Add(progress!); return pending.Task; } });
        vm.SetProfile(IdA); var first = vm.LoadAsync(false);
        reports[0].Report(new(2, 4000, 9000));
        await Until(() => vm.ProgressText == InventoryText.Format("ProgressReported", 2, 4000, 9000));
        vm.ContextId = "3"; var second = vm.LoadAsync(false); await first;
        reports[0].Report(new(99, 9999, 99999)); reports[1].Report(new(1, 2000, null));
        await Until(() => vm.ProgressText == InventoryText.Format("Progress", 1, 2000));
        Assert.DoesNotContain("999", vm.ProgressText);
        vm.Cancel(); await second; reports[1].Report(new(88, 8888, null)); await Task.Delay(30);
        Assert.Equal("", vm.ProgressText); Assert.Contains("cancelled", vm.StateMessage);
        pending.SetResult(Ready(new(IdA, 730, "2")));
    }

    [Fact]
    public async Task ExternalCancellationCompletesWithoutAStuckLoadingState()
    {
        var pending = Pending<Outcome<InventorySnapshot>>(); using var cancellation = new CancellationTokenSource();
        using var vm = Create(new Inventory { Get = (_, _, _, _) => pending.Task }); vm.SetProfile(IdA);
        var loading = vm.LoadAsync(false, cancellation.Token); cancellation.Cancel(); await loading.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(vm.IsBusy); Assert.True(vm.CanLoad); Assert.Contains("cancelled", vm.StateMessage);
        pending.SetResult(Ready(new(IdA, 730, "2"))); Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task NewerFilterWinsAndForeignSelectionCannotLoadImages()
    {
        var icons = new Icons(); using var vm = Create(icons: icons); vm.SetProfile(IdA); await vm.LoadAsync(false);
        vm.Query = "A polished"; var first = vm.FilteringTask; vm.Query = "B unusual"; await vm.FilteringTask; await first;
        Assert.Equal("102", Assert.Single(vm.Rows).AssetId);
        vm.SelectedItem = new(new("999", "999", "0", 1, "Foreign", null, "https://example.test/image", null, null, false));
        await vm.IconLoadingTask; Assert.Null(vm.SelectedItem); Assert.Equal(0, icons.Calls);
    }

    [Fact]
    public async Task DisposingCancelsLoadAndFutureActionsDoNotStartRequests()
    {
        var pending = Pending<Outcome<InventorySnapshot>>(); CancellationToken captured = default;
        var service = new Inventory { Get = (_, _, _, token) => { captured = token; return pending.Task; } };
        var vm = Create(service); vm.SetProfile(IdA); var load = vm.LoadAsync(false); vm.Dispose();
        await load.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(captured.IsCancellationRequested);
        vm.SetProfile(IdB); await vm.LoadAsync(true); await vm.ApplyFiltersAsync(); Assert.Single(service.Calls);
        pending.SetResult(Ready(new(IdA, 730, "2"))); Assert.Empty(vm.Rows); Assert.False(vm.HasProfile);
    }

    private static InventoryViewModel Create(Inventory? service = null, Icons? icons = null) => new(service ?? new(), icons ?? new());
    private static InventoryItem[] Items() => [
        new("101", "201", "0", 2, "A polished item", "Pistol", "https://community.fastly.steamstatic.com/economy/image/101", true, false, true),
        new("102", "202", "0", 6, "B unusual item", "Hat", "https://community.fastly.steamstatic.com/economy/image/102", false, true, true),
        new("103", "203", "0", 1, null, null, null, null, null, false)];
    private static Outcome<InventorySnapshot> Ready(InventoryRequest request) => Outcome<InventorySnapshot>.Ready(new(request, Items(), 3, true, 1));
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class Inventory : IInventoryService
    {
        public List<(InventoryRequest Request, bool Force)> Calls { get; } = [];
        public Func<InventoryRequest, bool, IProgress<InventoryProgress>?, CancellationToken, Task<Outcome<InventorySnapshot>>> Get { get; init; }
            = (request, _, _, _) => Task.FromResult(Ready(request));
        public Task<Outcome<InventorySnapshot>> GetInventoryAsync(InventoryRequest request, bool forceRefresh, IProgress<InventoryProgress>? progress, CancellationToken cancellationToken)
        { Calls.Add((request, forceRefresh)); return Get(request, forceRefresh, progress, cancellationToken); }
    }
    private sealed class Icons : IInventoryIconLoader
    {
        public int Calls { get; private set; }
        public Func<string?, CancellationToken, Task<Bitmap?>> Get { get; init; } = (_, _) => Task.FromResult<Bitmap?>(null);
        public Task<Bitmap?> LoadAsync(string? url, CancellationToken cancellationToken) { ++Calls; return Get(url, cancellationToken); }
    }
}
