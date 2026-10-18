using GamePulse.Core;
using System.Text.Json;

namespace GamePulse.Infrastructure;

/// <summary>The library transport is separate so existing profile-only clients remain compatible.</summary>
public interface ISteamLibraryClient
{
    Task<Outcome<OwnedLibrary>> GetLibraryAsync(string steamId, CancellationToken cancellationToken);
    Task<Outcome<RecentGames>> GetRecentAsync(string steamId, CancellationToken cancellationToken);
}

public sealed partial class SteamApiClient
{
    // A large library may exceed the small summary response limit. Still bound the network allocation.
    private const long LibraryResponseLimit = 16 * 1024 * 1024;

    public async Task<Outcome<OwnedLibrary>> GetLibraryAsync(string steamId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSteamId(steamId)) return Invalid<OwnedLibrary>();
        var result = await GetAsync<LibraryEnvelope>("IPlayerService/GetOwnedGames/v1/",
            "input_json=" + Uri.EscapeDataString(JsonSerializer.Serialize(new
                { steamid = steamId, include_appinfo = true, include_played_free_games = true })),
            cancellationToken, LibraryResponseLimit).ConfigureAwait(false);
        if (!result.IsReady) return CopyFailure<LibraryEnvelope, OwnedLibrary>(result);
        var response = result.Data!.Response;
        if (response is null) return Malformed<OwnedLibrary>();
        var mapped = MapLibraryGames(response, response.GameCount, cancellationToken);
        if (!mapped.HasData()) return CopyFailure<IReadOnlyList<LibraryGame>, OwnedLibrary>(mapped);
        var library = new OwnedLibrary(mapped.Data!, response.GameCount!.Value, IsComplete: true);
        return new(mapped.State, library, mapped.Message, clock.GetUtcNow());
    }

    public async Task<Outcome<RecentGames>> GetRecentAsync(string steamId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSteamId(steamId)) return Invalid<RecentGames>();
        // Omit count: the endpoint returns all available recently played games rather than a hidden cap.
        var result = await GetAsync<LibraryEnvelope>("IPlayerService/GetRecentlyPlayedGames/v1/",
            "input_json=" + Uri.EscapeDataString(JsonSerializer.Serialize(new { steamid = steamId })),
            cancellationToken, LibraryResponseLimit).ConfigureAwait(false);
        if (!result.IsReady) return CopyFailure<LibraryEnvelope, RecentGames>(result);
        var response = result.Data!.Response;
        if (response is null) return Malformed<RecentGames>();
        var mapped = MapLibraryGames(response, response.TotalCount, cancellationToken);
        if (!mapped.HasData()) return CopyFailure<IReadOnlyList<LibraryGame>, RecentGames>(mapped);
        return new(mapped.State, new(mapped.Data!, response.TotalCount!.Value), mapped.Message, clock.GetUtcNow());
    }

    private Outcome<IReadOnlyList<LibraryGame>> MapLibraryGames(LibraryResponse response, int? reportedCount,
        CancellationToken cancellationToken)
    {
        // {} can mean hidden game details, absence of permission or missing data. It does not prove privacy.
        if (reportedCount is null)
            return Outcome<IReadOnlyList<LibraryGame>>.Failure(DataState.Unavailable,
                "Steam did not expose a game count. Game details may be hidden or unavailable; this does not establish an empty library.");
        if (reportedCount < 0) return Malformed<IReadOnlyList<LibraryGame>>();
        if (reportedCount == 0 && response.Games is null or { Length: 0 })
            return new(DataState.Empty, Array.Empty<LibraryGame>(), "Steam explicitly returned no games.", clock.GetUtcNow());
        if (response.Games is null || response.Games.Length != reportedCount.Value)
            return Outcome<IReadOnlyList<LibraryGame>>.Failure(DataState.Unavailable,
                "Steam returned an incomplete game list. Refresh before using library totals or comparing profiles.");
        var games = new LibraryGame[response.Games.Length];
        var ids = new HashSet<uint>();
        for (var index = 0; index < response.Games.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var game = response.Games[index];
            if (game is null || game.AppId is null or 0 || game.PlaytimeMinutes < 0 ||
                game.RecentPlaytimeMinutes < 0 || game.LastPlayed < 0 || !ids.Add(game.AppId.Value))
                return Malformed<IReadOnlyList<LibraryGame>>();
            games[index] = new(game.AppId.Value, game.Name, game.PlaytimeMinutes,
                game.RecentPlaytimeMinutes, UnixTime(game.LastPlayed));
        }
        return Outcome<IReadOnlyList<LibraryGame>>.Ready(Array.AsReadOnly(games), clock.GetUtcNow());
    }
}
