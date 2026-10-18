using System.Globalization;
using GamePulse.Core;

namespace GamePulse.Infrastructure;

public interface ISteamAchievementClient
{
    Task<Outcome<AchievementSchema>> GetAchievementSchemaAsync(uint appId, CancellationToken cancellationToken);
    Task<Outcome<PlayerAchievements>> GetPlayerAchievementsAsync(string steamId, uint appId, CancellationToken cancellationToken);
}

public sealed partial class SteamApiClient
{
    // Local response-safety budget, not a Steam achievement-count limit. Excess fails explicitly.
    private const long AchievementResponseLimit = 8 * 1024 * 1024;

    public async Task<Outcome<AchievementSchema>> GetAchievementSchemaAsync(uint appId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (appId == 0) return InvalidApp<AchievementSchema>();
        var result = await GetAsync<AchievementSchemaEnvelope>("ISteamUserStats/GetSchemaForGame/v2/",
            "appid=" + appId.ToString(CultureInfo.InvariantCulture) + "&l=english", cancellationToken,
            AchievementResponseLimit).ConfigureAwait(false);
        if (!result.IsReady) return CopyFailure<AchievementSchemaEnvelope, AchievementSchema>(result);
        var game = result.Data!.Game;
        if (game?.Available?.Achievements is not { } rows)
            return Outcome<AchievementSchema>.Failure(DataState.Unavailable,
                "Steam did not expose an achievement schema. Missing schema fields do not establish that this game has no achievements.");
        var definitions = new AchievementDefinition[rows.Length];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[i];
            if (row is null || !ValidAchievementName(row.Name) || !names.Add(row.Name!) || row.Hidden is not (null or 0 or 1))
                return Malformed<AchievementSchema>();
            definitions[i] = new(row.Name!, row.DisplayName, row.Description, row.Hidden.HasValue ? row.Hidden == 1 : null);
        }
        var schema = new AchievementSchema(appId, game.GameName, Array.AsReadOnly(definitions));
        return rows.Length == 0
            ? new(DataState.NoAchievements, schema, "Steam explicitly returned an empty achievement schema.", clock.GetUtcNow())
            : Outcome<AchievementSchema>.Ready(schema, clock.GetUtcNow());
    }

    public async Task<Outcome<PlayerAchievements>> GetPlayerAchievementsAsync(string steamId, uint appId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSteamId(steamId)) return Invalid<PlayerAchievements>();
        if (appId == 0) return InvalidApp<PlayerAchievements>();
        var result = await GetAsync<PlayerAchievementsEnvelope>("ISteamUserStats/GetPlayerAchievements/v1/",
            "steamid=" + steamId + "&appid=" + appId.ToString(CultureInfo.InvariantCulture) + "&l=english",
            cancellationToken, AchievementResponseLimit, envelope => envelope.PlayerStats?.Success == false).ConfigureAwait(false);
        if (!result.IsReady) return CopyFailure<PlayerAchievementsEnvelope, PlayerAchievements>(result);
        var stats = result.Data!.PlayerStats;
        if (stats is null || stats.Success is null ||
            stats.SteamId is not null && stats.SteamId != steamId ||
            stats.GameId is not null && stats.GameId != appId.ToString(CultureInfo.InvariantCulture))
            return Malformed<PlayerAchievements>();
        if (stats.Success == false)
        {
            // The API does not publish a stable machine-readable privacy code. This exact English
            // response is best-effort only; generic HTTP denial is never labelled private.
            return stats.Error?.Trim().Equals("Profile is not public", StringComparison.OrdinalIgnoreCase) == true
                ? Outcome<PlayerAchievements>.Failure(DataState.Private, "Steam reports that this profile's achievements are not public.")
                : Outcome<PlayerAchievements>.Failure(DataState.Unavailable,
                    "Steam did not expose player achievements for this game. Privacy, ownership or game support may limit access.");
        }
        if (stats.Achievements is not { } rows) return Malformed<PlayerAchievements>();
        var achievements = new PlayerAchievement[rows.Length];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[i];
            if (row is null || !ValidAchievementName(row.ApiName) || !names.Add(row.ApiName!) ||
                row.Achieved is not (0 or 1) || row.UnlockTime < 0 || row.Achieved == 0 && row.UnlockTime > 0)
                return Malformed<PlayerAchievements>();
            var timestamp = UnixTime(row.UnlockTime);
            if (row.UnlockTime > 0 && timestamp is null) return Malformed<PlayerAchievements>();
            achievements[i] = new(row.ApiName!, row.Achieved == 1, timestamp);
        }
        return Outcome<PlayerAchievements>.Ready(new(appId, Array.AsReadOnly(achievements)), clock.GetUtcNow());
    }

    private static bool ValidAchievementName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim() == name;
    private static Outcome<T> InvalidApp<T>() => Outcome<T>.Failure(DataState.InvalidInput, "Enter a positive Steam AppID.");
}
