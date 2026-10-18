using GamePulse.Core;
using Xunit;

namespace GamePulse.UnitTests;

public class InventoryQueryTests
{
    private const string SteamId = "76561198000000001";
    private static InventoryItem Item(string id, string? name = "Equal", string? type = "Tool", ulong amount = 1,
        bool? marketable = true, bool? tradable = true, bool described = true) =>
        new(id, "1234", "0", amount, name, type, null, marketable, tradable, described);
    private static InventorySnapshot Snapshot(IReadOnlyList<InventoryItem> items, bool complete = true) =>
        new(new(SteamId, 730, "2"), items, null, complete, 1, complete ? null : DataState.RateLimited);

    [Fact]
    public void CatalogueIncludesAllSixRequestedAppsWithConfigurableDefaults()
    {
        Assert.Equal(new uint[] { 730, 440, 570, 578080, 252490, 753 }, InventoryCatalog.Apps.Select(x => x.AppId));
        foreach (var app in InventoryCatalog.Apps)
            Assert.True(InventoryRequestValidation.Validate(new(SteamId, app.AppId, app.DefaultContextId)).IsReady);
        Assert.Equal("6", Assert.Single(InventoryCatalog.Apps, x => x.AppId == 753).DefaultContextId);
        Assert.True(InventoryRequestValidation.Validate(new(SteamId, 730, "42")).IsReady);
        Assert.True(InventoryRequestValidation.Validate(new(SteamId, uint.MaxValue, ulong.MaxValue.ToString())).IsReady);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("0")] [InlineData("00")]
    [InlineData("02")] [InlineData(" 2")] [InlineData("2 ")] [InlineData("+2")] [InlineData("-2")]
    [InlineData("2/3")] [InlineData("2?key=secret")] [InlineData("https://steamcommunity.com")]
    [InlineData("٢")] [InlineData("18446744073709551616")]
    public void InvalidContextNeverBecomesUrlPath(string? context)
    {
        var result = InventoryRequestValidation.Validate(new(SteamId, 730, context!));
        Assert.Equal(DataState.InvalidInput, result.State);
        Assert.Null(result.Data);
        Assert.DoesNotContain("secret", result.Message ?? "");
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("some-vanity")]
    [InlineData("76561198000000000 ")] [InlineData(" 76561198000000000")]
    [InlineData("https://steamcommunity.com/profiles/76561198000000000")]
    [InlineData("76561197960265728")] [InlineData("103582791429521412")]
    public void InternalRequestRequiresCanonicalIndividualSteamId(string? id) =>
        Assert.Equal(DataState.InvalidInput, InventoryRequestValidation.Validate(new(id!, 730, "2")).State);

    [Fact]
    public void ZeroAppIdIsRejected() =>
        Assert.Equal(DataState.InvalidInput, InventoryRequestValidation.Validate(new(SteamId, 0, "2")).State);

    [Theory]
    [InlineData("0", false, false)] [InlineData("0", true, true)] [InlineData("1", false, true)]
    [InlineData("18446744073709551615", false, true)] [InlineData("01", true, false)]
    [InlineData("18446744073709551616", true, false)] [InlineData("1e2", true, false)]
    public void NumericIdValidationKeepsInstanceZeroDistinct(string id, bool allowZero, bool expected) =>
        Assert.Equal(expected, InventoryRequestValidation.IsDecimalId(id, allowZero));

    [Theory]
    [InlineData(" tool ")] [InlineData("rare")] [InlineData("9999")] [InlineData("1234")] [InlineData("0007")]
    public void SearchCoversNameTypeAndAllThreeIds(string query)
    {
        var item = Item("9999", "Rare Key", "TOOL") with { InstanceId = "0007" };
        Assert.Equal(item, Assert.Single(InventoryQuery.Apply([item, Item("2", "No", "Other") with { ClassId = "3" }], new(query))));
    }

    [Fact]
    public void NullQueryKeepsMissingMetadataAddressable()
    {
        var item = Item("321", null, null, described: false);
        Assert.Equal("321", InventoryQuery.Name(item));
        Assert.Equal("", InventoryQuery.Type(item));
        Assert.Equal(item, Assert.Single(InventoryQuery.Apply([item], new(null!))));
        Assert.Equal(item, Assert.Single(InventoryQuery.Apply([item], new("321"))));
        Assert.Empty(InventoryQuery.Apply([item], new("not found")));
    }

    [Fact]
    public void FlagFiltersRequireKnownTrueNeverUnknown()
    {
        var items = new[] { Item("1", marketable: true, tradable: true), Item("2", marketable: false),
            Item("3", marketable: null), Item("4", tradable: false), Item("5", tradable: null) };
        Assert.Equal(new[] { "1", "4", "5" }, InventoryQuery.Apply(items, new(MarketableOnly: true)).Select(x => x.AssetId));
        Assert.Equal(new[] { "1", "2", "3" }, InventoryQuery.Apply(items, new(TradableOnly: true)).Select(x => x.AssetId));
        Assert.Equal("1", Assert.Single(InventoryQuery.Apply(items, new(MarketableOnly: true, TradableOnly: true))).AssetId);
    }

    [Theory]
    [InlineData(InventorySort.Name)] [InlineData(InventorySort.Type)]
    [InlineData(InventorySort.AmountDescending)] [InlineData(InventorySort.AssetId)]
    public void SortTiesUseNumericAssetIds(InventorySort sort)
    {
        var items = new[] { Item("100"), Item("18446744073709551615"), Item("2"), Item("10") };
        Assert.Equal(new[] { "2", "10", "100", "18446744073709551615" },
            InventoryQuery.Apply(items, new(Sort: sort)).Select(x => x.AssetId));
    }

    [Fact]
    public void NameSortUsesCaseInsensitiveNamesWithUnknownLast()
    {
        var items = new[] { Item("2", null), Item("4", "zebra"), Item("3", "Apple"), Item("1", "apple"), Item("5", " ") };
        Assert.Equal(new[] { "1", "3", "4", "2", "5" }, InventoryQuery.Apply(items, new()).Select(x => x.AssetId));
    }

    [Fact]
    public void TypeSortPlacesUnknownTypesLastAndUsesNameWithinType()
    {
        var items = new[] { Item("2", "A", null), Item("3", "z", "Tool"), Item("1", "B", "badge"), Item("4", "A", "tool") };
        Assert.Equal(new[] { "1", "4", "3", "2" }, InventoryQuery.Apply(items, new(Sort: InventorySort.Type)).Select(x => x.AssetId));
    }

    [Fact]
    public void AmountSortUsesUnsignedQuantityWithoutNarrowing()
    {
        var items = new[] { Item("1", amount: 1), Item("2", amount: ulong.MaxValue), Item("3", amount: uint.MaxValue) };
        Assert.Equal(new[] { "2", "3", "1" }, InventoryQuery.Apply(items, new(Sort: InventorySort.AmountDescending)).Select(x => x.AssetId));
    }

    [Fact]
    public void QueryRetainsEveryAssetAndDoesNotMutateLargeSource()
    {
        var items = Enumerable.Range(1, 12_000).Select(i => Item(i.ToString(), $"Item {i:D5}")).Reverse().ToArray();
        var result = InventoryQuery.Apply(items, new());
        Assert.Equal(12_000, result.Count);
        Assert.Equal("1", result[0].AssetId);
        Assert.Equal("12000", result[^1].AssetId);
        Assert.Equal("12000", items[0].AssetId);
    }

    [Fact]
    public void EmptyCancelledAndInvalidQueriesHaveExplicitBehavior()
    {
        Assert.Empty(InventoryQuery.Apply([], new()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => InventoryQuery.Apply([], new(), cancelled.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryQuery.Apply([], new(Sort: (InventorySort)999)));
        Assert.Throws<ArgumentNullException>(() => InventoryQuery.Apply(null!, new()));
        Assert.Throws<ArgumentNullException>(() => InventoryQuery.Apply([], null!));
    }

    [Fact]
    public void StatisticsCountAssetsSeparatelyFromUnitsAndKnownFlags()
    {
        var snapshot = Snapshot([Item("1", amount: 3, marketable: true, tradable: false),
            Item("2", amount: 7, marketable: null, tradable: true, described: false),
            Item("3", amount: 11, marketable: false, tradable: null)]);
        var stats = InventoryStatistics.Calculate(snapshot);
        Assert.Equal(3, stats.AssetCount);
        Assert.Equal(21m, stats.LoadedUnits); Assert.Equal(21m, stats.TotalUnits);
        Assert.Equal(3m, stats.MarketableUnits); Assert.Equal(7m, stats.TradableUnits);
        Assert.Equal(2, stats.MetadataCount); Assert.Equal(2, stats.MarketableKnownCount); Assert.Equal(2, stats.TradableKnownCount);
    }

    [Fact]
    public void PartialSnapshotRetainsLoadedCountsButNeverEstablishesGlobalTotal()
    {
        var stats = InventoryStatistics.Calculate(Snapshot([Item("1", amount: 5)], complete: false));
        Assert.Equal(1, stats.AssetCount); Assert.Equal(5m, stats.LoadedUnits); Assert.Null(stats.TotalUnits);
        Assert.Null(InventoryStatistics.Calculate(Snapshot([], complete: false)).TotalUnits);
        Assert.Equal(0m, InventoryStatistics.Calculate(Snapshot([])).TotalUnits);
    }

    [Fact]
    public void ExactQuantitySumDoesNotOverflowUInt64()
    {
        var items = Enumerable.Range(1, 100_000).Select(i => Item(i.ToString(), amount: ulong.MaxValue)).ToArray();
        var stats = InventoryStatistics.Calculate(Snapshot(items));
        var expected = (decimal)ulong.MaxValue * items.Length;
        Assert.Equal(expected, stats.LoadedUnits); Assert.Equal(expected, stats.TotalUnits);
        Assert.Equal(expected, stats.MarketableUnits); Assert.Equal(expected, stats.TradableUnits);
    }

    [Fact]
    public void CancelledStatisticsNeverPresentAnIncompleteSum()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => InventoryStatistics.Calculate(Snapshot([]), cancelled.Token));
    }
}
