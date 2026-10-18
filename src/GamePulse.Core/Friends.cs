namespace GamePulse.Core;

/// <summary>Summary absence never implies an offline/nonexistent/private friend.</summary>
public sealed record SteamFriend(string SteamId, DateTimeOffset? FriendsSince,
    SteamProfile? Profile, DataState SummaryState);

/// <summary>All validated friend IDs are retained even when summary enrichment is partial.</summary>
public sealed record FriendDirectory(IReadOnlyList<SteamFriend> Friends,
    bool SummariesComplete, int FailedSummaryBatches);

public interface IFriendsService
{
    Task<Outcome<FriendDirectory>> GetFriendsAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken);
}

public enum FriendSort { Status, Name, FriendsSinceNewest, FriendsSinceOldest }
public sealed record FriendFilter(string Query = "", FriendSort Sort = FriendSort.Status, bool OnlineOnly = false);

public sealed record FriendAction(string SteamId, FriendDestination Destination);
public enum FriendDestination { Explorer, CompareA, CompareB }
