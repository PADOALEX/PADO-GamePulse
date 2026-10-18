namespace GamePulse.Core;

/// <summary>Only facts supplied by Steam. Missing playtime is unknown, never zero.</summary>
public sealed record LibraryGame(uint AppId, string? Name, long? PlaytimeMinutes,
    long? RecentPlaytimeMinutes, DateTimeOffset? LastPlayed);

public sealed record OwnedLibrary(IReadOnlyList<LibraryGame> Games, int ReportedCount, bool IsComplete);
public sealed record RecentGames(IReadOnlyList<LibraryGame> Games, int ReportedCount);

public interface ILibraryService
{
    Task<Outcome<OwnedLibrary>> GetLibraryAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken);
    Task<Outcome<RecentGames>> GetRecentAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken);
}

public static class Availability
{
    public static bool HasData<T>(this Outcome<T>? result) => result is { Data: not null, State: DataState.Ready or DataState.Empty };
}

public enum LibrarySort { MostPlayed, Name, LeastPlayed, RecentlyPlayed }
public sealed record LibraryFilter(string Query = "", double? MinimumHours = null,
    double? MaximumHours = null, bool UnplayedOnly = false, LibrarySort Sort = LibrarySort.MostPlayed);

public sealed record MutualGame(uint AppId, string? Name, long? MinutesA, long? MinutesB);
public sealed record ProfileComparison(IReadOnlyList<MutualGame> MutualGames,
    int GamesA, int GamesB, long? TotalMinutesA, long? TotalMinutesB);

public interface IProfileComparisonService
{
    Outcome<ProfileComparison> Compare(Outcome<OwnedLibrary>? libraryA, Outcome<OwnedLibrary>? libraryB);
}
