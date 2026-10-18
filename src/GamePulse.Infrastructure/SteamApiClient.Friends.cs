using GamePulse.Core;

namespace GamePulse.Infrastructure;

/// <summary>Transport contracts keep batching and session caching independent from JSON parsing.</summary>
public interface ISteamFriendsClient
{
    Task<Outcome<IReadOnlyList<SteamFriend>>> GetFriendListAsync(string steamId, CancellationToken cancellationToken);
    Task<Outcome<IReadOnlyList<SteamProfile>>> GetPlayerSummariesAsync(IReadOnlyList<string> steamIds, CancellationToken cancellationToken);
}

public sealed partial class SteamApiClient
{
    // Documented GetPlayerSummaries limit, not an invented requests/time quota.
    public const int PlayerSummaryBatchSize = 100;
    private const long FriendsResponseLimit = 8 * 1024 * 1024;

    public async Task<Outcome<IReadOnlyList<SteamFriend>>> GetFriendListAsync(string steamId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSteamId(steamId)) return Invalid<IReadOnlyList<SteamFriend>>();
        var result = await GetAsync<FriendListEnvelope>("ISteamUser/GetFriendList/v1/",
            "steamid=" + Uri.EscapeDataString(steamId) + "&relationship=friend", cancellationToken,
            FriendsResponseLimit).ConfigureAwait(false);
        if (!result.IsReady) return CopyFailure<FriendListEnvelope, IReadOnlyList<SteamFriend>>(result);
        var response = result.Data!.FriendsList;
        // An omitted friends array does not establish an empty or private friends list.
        if (response?.Friends is null)
            return Outcome<IReadOnlyList<SteamFriend>>.Failure(DataState.Unavailable,
                "Steam did not expose a friends list. Privacy or API permission may limit access; this does not establish an empty list.");
        if (response.Friends.Length == 0)
            return new(DataState.Empty, Array.Empty<SteamFriend>(), "Steam explicitly returned no friends.", clock.GetUtcNow());
        var friends = new SteamFriend[response.Friends.Length];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < response.Friends.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var friend = response.Friends[index];
            if (friend?.SteamId is not { } id || !IsSteamId(id) || !ids.Add(id) ||
                friend.Relationship != "friend" || friend.FriendsSince < 0)
                return Malformed<IReadOnlyList<SteamFriend>>();
            friends[index] = new(id, UnixTime(friend.FriendsSince), null, DataState.Unavailable);
        }
        return Outcome<IReadOnlyList<SteamFriend>>.Ready(Array.AsReadOnly(friends), clock.GetUtcNow());
    }

    public async Task<Outcome<IReadOnlyList<SteamProfile>>> GetPlayerSummariesAsync(IReadOnlyList<string> steamIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (steamIds.Count > PlayerSummaryBatchSize || steamIds.Any(id => id is null || !IsSteamId(id)) ||
            steamIds.Distinct(StringComparer.Ordinal).Count() != steamIds.Count)
            return Outcome<IReadOnlyList<SteamProfile>>.Failure(DataState.InvalidInput,
                "Request between 1 and 100 distinct public individual SteamIDs per summary batch.");
        if (steamIds.Count == 0)
            return Outcome<IReadOnlyList<SteamProfile>>.Ready(Array.Empty<SteamProfile>(), clock.GetUtcNow());
        var requested = steamIds.ToHashSet(StringComparer.Ordinal);
        var result = await GetAsync<PlayerEnvelope>("ISteamUser/GetPlayerSummaries/v2/",
            "steamids=" + Uri.EscapeDataString(string.Join(',', steamIds)), cancellationToken,
            FriendsResponseLimit).ConfigureAwait(false);
        if (!result.IsReady) return CopyFailure<PlayerEnvelope, IReadOnlyList<SteamProfile>>(result);
        if (result.Data!.Response?.Players is not { } players) return Malformed<IReadOnlyList<SteamProfile>>();
        var profiles = new SteamProfile[players.Length];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < players.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var player = players[index];
            if (player?.SteamId is not { } id || !requested.Contains(id) || !seen.Add(id))
                return Malformed<IReadOnlyList<SteamProfile>>();
            profiles[index] = new(id, player.Name, "https://steamcommunity.com/profiles/" + id,
                SafeAvatar(player.Avatar), player.Visibility, player.PersonaState,
                UnixTime(player.Created), UnixTime(player.LastLogoff), player.CurrentGame);
        }
        // Missing profiles stay missing. The service preserves those IDs with explicit unknown summaries.
        return Outcome<IReadOnlyList<SteamProfile>>.Ready(Array.AsReadOnly(profiles), clock.GetUtcNow());
    }
}
