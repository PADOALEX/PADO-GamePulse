using System.Globalization;

namespace GamePulse.Core;

public enum InventorySort { Name, Type, AmountDescending, AssetId }
public sealed record InventoryFilter(string Query = "", InventorySort Sort = InventorySort.Name,
    bool MarketableOnly = false, bool TradableOnly = false);

/// <summary>Validates path components before a public inventory URL can be constructed.</summary>
public static class InventoryRequestValidation
{
    public static Outcome<InventoryRequest> Validate(InventoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parsed = ProfileInput.Parse(request.SteamId);
        if (parsed.Data is not { Kind: ProfileInputKind.SteamId } input || input.Value != request.SteamId ||
            request.AppId == 0 || !IsDecimalId(request.ContextId))
            return Outcome<InventoryRequest>.Failure(DataState.InvalidInput,
                "Inventory requires a valid SteamID64, positive AppID and positive numeric context ID.");
        return Outcome<InventoryRequest>.Ready(request);
    }

    /// <summary>Canonical unsigned decimal IDs; only instance IDs may explicitly allow zero.</summary>
    public static bool IsDecimalId(string? value, bool allowZero = false) =>
        value is { Length: > 0 and <= 20 } && (value.Length == 1 || value[0] != '0') &&
        value.All(char.IsAsciiDigit) && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
        (allowZero || id > 0);
}

/// <summary>Filters the entire loaded source; unavailable descriptions and flags stay unknown.</summary>
public static class InventoryQuery
{
    public static string Name(InventoryItem item) => string.IsNullOrWhiteSpace(item.Name) ? item.AssetId : item.Name;
    public static string Type(InventoryItem item) => string.IsNullOrWhiteSpace(item.Type) ? "" : item.Type;

    public static IReadOnlyList<InventoryItem> Apply(IReadOnlyList<InventoryItem> items, InventoryFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(filter);
        if (!Enum.IsDefined(filter.Sort)) throw new ArgumentOutOfRangeException(nameof(filter));
        cancellationToken.ThrowIfCancellationRequested();
        var query = filter.Query?.Trim() ?? "";
        var selected = new List<InventoryItem>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filter.MarketableOnly && item.Marketable != true) continue;
            if (filter.TradableOnly && item.Tradable != true) continue;
            if (query.Length > 0 && !Contains(item.Name, query) && !Contains(item.Type, query) &&
                !Contains(item.AssetId, query) && !Contains(item.ClassId, query) && !Contains(item.InstanceId, query)) continue;
            selected.Add(item);
        }
        IOrderedEnumerable<InventoryItem> ordered = filter.Sort switch
        {
            InventorySort.Type => selected.OrderBy(x => string.IsNullOrWhiteSpace(x.Type))
                .ThenBy(Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => string.IsNullOrWhiteSpace(x.Name)).ThenBy(Name, StringComparer.OrdinalIgnoreCase),
            InventorySort.AmountDescending => selected.OrderByDescending(x => x.Amount),
            InventorySort.AssetId => selected.OrderBy(x => x.AssetId, AssetIdComparer.Instance),
            _ => selected.OrderBy(x => string.IsNullOrWhiteSpace(x.Name)).ThenBy(Name, StringComparer.OrdinalIgnoreCase)
        };
        // ID tie breaking makes ordering deterministic across pages, including numeric IDs of different lengths.
        var result = ordered.ThenBy(x => x.AssetId, AssetIdComparer.Instance).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static bool Contains(string? value, string query) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private sealed class AssetIdComparer : IComparer<string>
    {
        public static AssetIdComparer Instance { get; } = new();
        public int Compare(string? a, string? b)
        {
            if (ulong.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var first) &&
                ulong.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var second))
            {
                var order = first.CompareTo(second);
                if (order != 0) return order;
            }
            return StringComparer.Ordinal.Compare(a, b);
        }
    }
}

/// <summary>Flag unit counts describe only known-true flags among loaded assets, never inferred global totals.</summary>
public sealed record InventoryStatistics(int AssetCount, decimal LoadedUnits, decimal? TotalUnits,
    decimal MarketableUnits, decimal TradableUnits, int MetadataCount, int MarketableKnownCount, int TradableKnownCount)
{
    public static InventoryStatistics Calculate(InventorySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        decimal loaded = 0, marketable = 0, tradable = 0;
        var metadataCount = 0; var marketableKnown = 0; var tradableKnown = 0;
        foreach (var item in snapshot.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Decimal has room for ulong.MaxValue quantities across every row in an int-sized collection.
            loaded += item.Amount;
            if (item.Marketable == true) marketable += item.Amount;
            if (item.Tradable == true) tradable += item.Amount;
            if (item.HasDescription) metadataCount++;
            if (item.Marketable.HasValue) marketableKnown++;
            if (item.Tradable.HasValue) tradableKnown++;
        }
        return new(snapshot.Items.Count, loaded, snapshot.IsComplete ? loaded : null, marketable, tradable,
            metadataCount, marketableKnown, tradableKnown);
    }
}
