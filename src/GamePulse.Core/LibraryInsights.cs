namespace GamePulse.Core;

public enum PlaytimeRange { Unplayed, UnderOneHour, OneToTenHours, TenToFiftyHours, FiftyToHundredHours, HundredPlusHours, Unknown }

public sealed record PlaytimeBucket(PlaytimeRange Range, int Count, double ShareOfAvailableGames);

/// <summary>Local facts about an already loaded snapshot. Whole-library claims require full coverage.</summary>
public sealed record LibraryInsightSnapshot(int AvailableCount, int ReportedCount, bool IsComplete,
    int KnownPlaytimeCount, decimal KnownMinutes, decimal? TotalMinutes, int? UnplayedCount,
    double? UnplayedShare, double? TopThreePlaytimeShare, IReadOnlyList<PlaytimeBucket> Buckets,
    IReadOnlyList<LibraryGame> TopGames);

public static class LibraryInsights
{
    public static LibraryInsightSnapshot Calculate(OwnedLibrary library, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(library.Games);
        var counts = new int[7];
        var known = new List<LibraryGame>(library.Games.Count);
        decimal knownMinutes = 0;
        foreach (var game in library.Games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var minutes = game.PlaytimeMinutes;
            var range = minutes switch
            {
                null or < 0 => PlaytimeRange.Unknown,
                0 => PlaytimeRange.Unplayed,
                < 60 => PlaytimeRange.UnderOneHour,
                < 600 => PlaytimeRange.OneToTenHours,
                < 3000 => PlaytimeRange.TenToFiftyHours,
                < 6000 => PlaytimeRange.FiftyToHundredHours,
                _ => PlaytimeRange.HundredPlusHours
            };
            counts[(int)range]++;
            if (minutes is not >= 0) continue;
            knownMinutes += minutes.Value; // Decimal avoids overflow when summing Int64 values.
            known.Add(game);
        }
        var available = library.Games.Count;
        var complete = library.IsComplete && library.ReportedCount == available;
        var completePlaytime = complete && known.Count == available;
        var sorted = LibraryQuery.Apply(known, new LibraryFilter(), cancellationToken);
        decimal topThreeMinutes = sorted.Take(3).Sum(game => (decimal)game.PlaytimeMinutes!.Value);
        var buckets = Enum.GetValues<PlaytimeRange>().Select(range => new PlaytimeBucket(range,
            counts[(int)range], available > 0 ? counts[(int)range] * 100d / available : 0)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(available, library.ReportedCount, complete, known.Count, knownMinutes,
            completePlaytime ? knownMinutes : null,
            completePlaytime ? counts[(int)PlaytimeRange.Unplayed] : null,
            completePlaytime && available > 0 ? counts[(int)PlaytimeRange.Unplayed] * 100d / available : null,
            completePlaytime && knownMinutes > 0 ? (double)(topThreeMinutes / knownMinutes * 100m) : null,
            buckets, sorted.Take(10).ToArray());
    }
}
