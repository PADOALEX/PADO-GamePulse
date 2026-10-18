using System.Globalization;

namespace GamePulse.Core;

/// <summary>Pure, cancellable querying. An absent playtime never satisfies a numeric filter.</summary>
public static class LibraryQuery
{
    public static IReadOnlyList<LibraryGame> Apply(IReadOnlyList<LibraryGame> games,
        LibraryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(filter);
        ValidateBound(filter.MinimumHours, nameof(filter.MinimumHours));
        ValidateBound(filter.MaximumHours, nameof(filter.MaximumHours));
        if (filter.MinimumHours > filter.MaximumHours)
            throw new ArgumentException("Minimum hours cannot exceed maximum hours.", nameof(filter));
        if (!Enum.IsDefined(filter.Sort)) throw new ArgumentOutOfRangeException(nameof(filter));
        var query = filter.Query?.Trim() ?? "";
        var numericFilter = filter.MinimumHours.HasValue || filter.MaximumHours.HasValue || filter.UnplayedOnly;
        var result = new List<LibraryGame>(games.Count);
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query.Length != 0 && !(game.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                && !game.AppId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal)) continue;
            var minutes = game.PlaytimeMinutes is >= 0 ? game.PlaytimeMinutes : null;
            if (numericFilter && !minutes.HasValue) continue;
            if (filter.UnplayedOnly && minutes != 0) continue;
            if (filter.MinimumHours.HasValue && minutes / 60d < filter.MinimumHours) continue;
            if (filter.MaximumHours.HasValue && minutes / 60d > filter.MaximumHours) continue;
            result.Add(game);
        }
        result.Sort((a, b) => Compare(a, b, filter.Sort));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static void ValidateBound(double? value, string name)
    {
        if (value.HasValue && (!double.IsFinite(value.Value) || value.Value < 0))
            throw new ArgumentOutOfRangeException(name, "Hours must be finite and non-negative.");
    }

    private static int Compare(LibraryGame a, LibraryGame b, LibrarySort sort)
    {
        int comparison;
        if (sort == LibrarySort.Name)
        {
            var missingA = string.IsNullOrWhiteSpace(a.Name);
            var missingB = string.IsNullOrWhiteSpace(b.Name);
            comparison = missingA ? missingB ? 0 : 1 : missingB ? -1 :
                StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        }
        else if (sort == LibrarySort.RecentlyPlayed)
        {
            comparison = a.LastPlayed.HasValue
                ? b.LastPlayed.HasValue ? b.LastPlayed.Value.CompareTo(a.LastPlayed.Value) : -1
                : b.LastPlayed.HasValue ? 1 : 0;
        }
        else
        {
            var minutesA = a.PlaytimeMinutes is >= 0 ? a.PlaytimeMinutes : null;
            var minutesB = b.PlaytimeMinutes is >= 0 ? b.PlaytimeMinutes : null;
            comparison = minutesA.HasValue
                ? minutesB.HasValue
                    ? sort == LibrarySort.MostPlayed
                        ? minutesB.Value.CompareTo(minutesA.Value)
                        : minutesA.Value.CompareTo(minutesB.Value)
                    : -1
                : minutesB.HasValue ? 1 : 0;
        }
        if (comparison == 0)
            comparison = StringComparer.OrdinalIgnoreCase.Compare(a.Name ?? "", b.Name ?? "");
        return comparison == 0 ? a.AppId.CompareTo(b.AppId) : comparison;
    }

}
