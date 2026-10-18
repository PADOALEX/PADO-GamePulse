namespace GamePulse.Core;

public sealed record InventoryApp(uint AppId, string Name, string DefaultContextId);
public static class InventoryCatalog
{
    // Editable defaults for the public contexts; availability still depends on Steam and the profile.
    public static IReadOnlyList<InventoryApp> Apps { get; } = Array.AsReadOnly(new InventoryApp[]
    {
        new(730, "Counter-Strike 2", "2"), new(440, "Team Fortress 2", "2"),
        new(570, "Dota 2", "2"), new(578080, "PUBG", "2"), new(252490, "Rust", "2"),
        new(753, "Steam Community", "6")
    });
}

public sealed record InventoryRequest(string SteamId, uint AppId, string ContextId);
public sealed record InventoryItem(string AssetId, string ClassId, string InstanceId, ulong Amount,
    string? Name, string? Type, string? IconUrl, bool? Marketable, bool? Tradable, bool HasDescription);
public enum InventoryStopReason { PageLimit, SizeLimit, SourceChanged, InvalidPagination, MissingTotal }

/// <summary>Rows are assets, quantities include amount. A partial snapshot never establishes global totals.</summary>
public sealed record InventorySnapshot(InventoryRequest Request, IReadOnlyList<InventoryItem> Items,
    long? ReportedAssetCount, bool IsComplete, int PagesLoaded, DataState? InterruptionState = null,
    InventoryStopReason? StopReason = null);
public sealed record InventoryProgress(int PagesLoaded, int AssetsLoaded, long? ReportedAssetCount);

public interface IInventoryService
{
    Task<Outcome<InventorySnapshot>> GetInventoryAsync(InventoryRequest request, bool forceRefresh,
        IProgress<InventoryProgress>? progress, CancellationToken cancellationToken);
}
