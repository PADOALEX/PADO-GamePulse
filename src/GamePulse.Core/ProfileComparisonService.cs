namespace GamePulse.Core;

/// <summary>Intersects two explicitly available, complete libraries. Unknown data is never zero.</summary>
public sealed class ProfileComparisonService : IProfileComparisonService
{
    public Outcome<ProfileComparison> Compare(Outcome<OwnedLibrary>? libraryA, Outcome<OwnedLibrary>? libraryB)
    {
        if (libraryA?.State == DataState.Private || libraryB?.State == DataState.Private)
            return Outcome<ProfileComparison>.Failure(DataState.Private, "A library is private. Mutual games cannot be determined.");
        if (libraryA?.State == DataState.Loading || libraryB?.State == DataState.Loading)
            return Outcome<ProfileComparison>.Failure(DataState.Loading, "Waiting for both libraries.");
        if (!libraryA.HasData() || !libraryB.HasData())
            return Outcome<ProfileComparison>.Failure(DataState.Unavailable, "Both libraries must be available to determine mutual games.");
        if (!libraryA!.Data!.IsComplete || !libraryB!.Data!.IsComplete)
            return Outcome<ProfileComparison>.Failure(DataState.Unavailable, "A library is incomplete. Mutual games cannot be determined reliably.");

        // Defensive deduplication prevents repeated AppIDs from inflating counts and playtime.
        var gamesA = libraryA.Data.Games.DistinctBy(game => game.AppId).ToArray();
        var gamesB = libraryB.Data.Games.DistinctBy(game => game.AppId).ToDictionary(game => game.AppId);
        var mutual = gamesA.Where(game => gamesB.ContainsKey(game.AppId))
            .Select(game => new MutualGame(game.AppId,
                string.IsNullOrWhiteSpace(game.Name) ? gamesB[game.AppId].Name : game.Name,
                game.PlaytimeMinutes, gamesB[game.AppId].PlaytimeMinutes))
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ThenBy(game => game.AppId).ToArray();
        var result = new ProfileComparison(mutual, gamesA.Length, gamesB.Count,
            TotalMinutes(gamesA), TotalMinutes(gamesB.Values));
        var timestamp = new[] { libraryA.UpdatedAt, libraryB.UpdatedAt }.Where(value => value.HasValue).Min();
        return new(mutual.Length == 0 ? DataState.Empty : DataState.Ready, result, UpdatedAt: timestamp,
            FromCache: libraryA.FromCache || libraryB.FromCache);
    }

    private static long? TotalMinutes(IEnumerable<LibraryGame> games)
    {
        long total = 0;
        foreach (var game in games)
        {
            if (game.PlaytimeMinutes is not { } minutes || minutes < 0) return null;
            try { total = checked(total + minutes); }
            catch (OverflowException) { return null; }
        }
        return total;
    }
}
