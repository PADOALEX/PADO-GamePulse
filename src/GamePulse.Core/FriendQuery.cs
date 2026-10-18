namespace GamePulse.Core;

/// <summary>Pure queries over the entire directory. Missing summaries remain addressable by SteamID.</summary>
public static class FriendQuery
{
    public static bool HasSummary(SteamFriend friend) => friend.SummaryState == DataState.Ready && friend.Profile is not null;
    public static bool IsOnline(SteamFriend friend) => HasSummary(friend) && friend.Profile!.PersonaState is >= 1 and <= 6;
    public static string Name(SteamFriend friend) => HasSummary(friend) && !string.IsNullOrWhiteSpace(friend.Profile!.DisplayName)
        ? friend.Profile.DisplayName : friend.SteamId;

    public static IReadOnlyList<SteamFriend> Apply(IReadOnlyList<SteamFriend> friends, FriendFilter filter,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = filter.Query?.Trim() ?? "";
        var selected = new List<SteamFriend>(friends.Count);
        foreach (var friend in friends)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filter.OnlineOnly && !IsOnline(friend)) continue;
            if (query.Length > 0 && !Name(friend).Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !friend.SteamId.Contains(query, StringComparison.Ordinal)) continue;
            selected.Add(friend);
        }
        var nameComparer = Comparer<string>.Create((a, b) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StringComparer.OrdinalIgnoreCase.Compare(a, b);
        });
        // Unknown dates and unknown presence sort last, without manufacturing values.
        var ordered = filter.Sort switch
        {
            FriendSort.Name => selected.OrderBy(Name, nameComparer),
            FriendSort.FriendsSinceNewest => selected.OrderBy(x => !x.FriendsSince.HasValue)
                .ThenByDescending(x => x.FriendsSince).ThenBy(Name, nameComparer),
            FriendSort.FriendsSinceOldest => selected.OrderBy(x => !x.FriendsSince.HasValue)
                .ThenBy(x => x.FriendsSince).ThenBy(Name, nameComparer),
            _ => selected.OrderBy(StatusRank).ThenBy(Name, nameComparer)
        };
        SteamFriend[] result;
        try { result = ordered.ThenBy(x => x.SteamId, StringComparer.Ordinal).ToArray(); }
        catch (InvalidOperationException exception) when (exception.InnerException is OperationCanceledException &&
            cancellationToken.IsCancellationRequested)
        {
            // The runtime wraps comparer failures. Preserve cancellation for callers so
            // a superseded search cannot become a visible filter error.
            throw new OperationCanceledException(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static int StatusRank(SteamFriend friend) => !HasSummary(friend) ? 8 : friend.Profile!.PersonaState switch
    {
        >= 1 and <= 6 => friend.Profile.PersonaState.Value - 1,
        0 => 6,
        _ => 8
    };
}
