using System.Globalization;
using GamePulse.Core;
using GamePulse.Desktop.ViewModels;

namespace GamePulse.Desktop.Services;

/// <summary>Exports only the current filtered data, retaining source identity, timestamps and coverage.</summary>
public static class AnalysisExport
{
    public static ExportTable? Capture(MainWindowViewModel model)
    {
        if (model.IsGames && model.Games is { IsFiltering: false, ActiveLoading: false, HasFilterError: false } games
            && games.Rows.Count > 0)
        {
            var kind = games.ShowRecent ? "recent-games" : "games";
            var state = games.ShowRecent ? games.RecentState : games.OwnedState;
            if (state is not (DataState.Ready or DataState.Empty) || !model.HasProfile) return null;
            var complete = games.ShowRecent ? "recent_activity_only" : games.OwnedSnapshot?.Data?.IsComplete == true ? "complete_returned_library" : "partial_returned_library";
            var updated = games.ShowRecent ? games.RecentSnapshot?.UpdatedAt : games.OwnedSnapshot?.UpdatedAt;
            var profile = DataExport.Identifier(model.SteamId);
            return new(kind,
                ["steam_id", "scope", "coverage", "source_updated_utc", "app_id", "name", "lifetime_minutes", "last_14_days_minutes", "last_played_utc"],
                games.Rows.Select(row => (IReadOnlyList<string?>)new string?[] { profile, "filtered_" + kind, complete, DataExport.Timestamp(updated),
                    row.AppId.ToString(CultureInfo.InvariantCulture), row.Game.Name, DataExport.Number(row.Game.PlaytimeMinutes),
                    DataExport.Number(row.Game.RecentPlaytimeMinutes), DataExport.Timestamp(row.Game.LastPlayed) }).ToArray());
        }
        if (model.IsComparison && model.Comparison is { IsBusy: false, IsFiltering: false, HasFilterError: false, HasComparison: true } comparison
            && comparison.State is DataState.Ready or DataState.Empty && comparison.MutualGames.Count > 0)
        {
            var idA = DataExport.Identifier(comparison.SideA.SteamId);
            var idB = DataExport.Identifier(comparison.SideB.SteamId);
            var timeA = DataExport.Timestamp(comparison.SideA.LibraryResult?.UpdatedAt);
            var timeB = DataExport.Timestamp(comparison.SideB.LibraryResult?.UpdatedAt);
            // The row's display name can be an AppID fallback. Export only a name actually supplied by Steam.
            var sourceNames = comparison.SideA.LibraryResult!.Data!.Games
                .Concat(comparison.SideB.LibraryResult!.Data!.Games).GroupBy(game => game.AppId)
                .ToDictionary(group => group.Key, group => group.FirstOrDefault(game => !string.IsNullOrWhiteSpace(game.Name))?.Name);
            return new("mutual-games",
                ["profile_a_steam_id", "profile_b_steam_id", "scope", "profile_a_updated_utc", "profile_b_updated_utc", "app_id", "name", "profile_a_minutes", "profile_b_minutes"],
                comparison.MutualGames.Select(row => (IReadOnlyList<string?>)new string?[] { idA, idB, "filtered_mutual_games", timeA, timeB,
                    row.AppId.ToString(CultureInfo.InvariantCulture), sourceNames.GetValueOrDefault(row.AppId), DataExport.Number(row.MinutesA), DataExport.Number(row.MinutesB) }).ToArray());
        }
        if (model.IsInventory && model.Inventory is { IsBusy: false, IsFiltering: false, HasSnapshot: true, HasFilterError: false } inventory
            && inventory.Rows.Count > 0 && inventory.Snapshot?.Data is { } snapshot)
        {
            var request = snapshot.Request;
            var profile = DataExport.Identifier(request.SteamId);
            var app = request.AppId.ToString(CultureInfo.InvariantCulture);
            var updated = DataExport.Timestamp(inventory.Snapshot.UpdatedAt);
            var coverage = snapshot.IsComplete ? "complete_returned_inventory" : "partial_returned_inventory";
            return new("inventory",
                ["steam_id", "app_id", "context_id", "scope", "coverage", "source_updated_utc", "asset_id", "class_id", "instance_id", "name", "type", "amount", "marketable", "tradable", "description_available"],
                inventory.Rows.Select(row => (IReadOnlyList<string?>)new string?[] { profile, app, DataExport.Identifier(request.ContextId), "filtered_inventory", coverage, updated,
                    DataExport.Identifier(row.AssetId), DataExport.Identifier(row.ClassId), DataExport.Identifier(row.InstanceId), row.Item.Name, row.Item.Type,
                    row.Item.Amount.ToString(CultureInfo.InvariantCulture), DataExport.Flag(row.Item.Marketable), DataExport.Flag(row.Item.Tradable), DataExport.Flag(row.Item.HasDescription) }).ToArray());
        }
        return null;
    }
}
