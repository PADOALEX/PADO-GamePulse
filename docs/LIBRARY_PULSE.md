# Library Pulse

Library Pulse derives playtime insights from the Profile Explorer's already loaded, unfiltered library. It does not issue additional Steam requests, store historical snapshots, infer genres or make ownership claims about hidden games. Switching the Games page to recent activity, searching or applying a Games filter does not change the underlying insight snapshot.

## Metrics and coverage

- **Available / reported** compares the number of returned games with Steam's reported count.
- **Total playtime** adds lifetime minutes only when the library is complete, its reported and returned counts match, and every returned game has nonnegative playtime.
- **Never played** uses exactly zero lifetime minutes under that same full-coverage requirement. The share divides this count by the number of games. It is unavailable for an empty library because that percentage has no denominator.
- **Top 3 share of playtime** divides the three highest lifetime minute values by total lifetime minutes, under the same full-coverage requirement. It is unavailable when all playtime is zero.
- **Time in returned games with known playtime** is an explicitly scoped subtotal. It can be shown for a partial library without claiming to be its total.

The distribution includes every returned game. Its non-overlapping minute ranges are 0, 1–59, 60–599, 600–2,999, 3,000–5,999 and 6,000 or more. Missing or negative minutes have their own **Unknown playtime** group. The bars use the number of returned games as their denominator, including the unknown group.

The ranked list is explicitly a **Top 10 by playtime**, including known zero-minute values if fewer than ten games have positive time. The Games page retains the full available list. Names and AppIDs break tied playtime values consistently. Store links use numeric AppIDs and HTTPS.

## Availability and responsiveness

Private, unavailable, missing-key, rate-limited and failed libraries retain distinct messages and unavailable metrics. A genuinely complete empty library has zero counts and total hours. A new profile or loading state immediately clears the previous snapshot. Calculations run outside the UI thread, support cancellation and suppress stale results. The ranked list has a finite viewport and a virtualizing panel.

The timestamp is the source library snapshot's timestamp; a cached snapshot is identified as such. English and Ukrainian resource files contain the same keys and formatting tokens. Changing the UI language updates presentation without fetching Steam data again.

## Validation

`LibraryInsightsTests` covers exact totals and concentration, all bucket boundaries, unknown and negative time, incomplete or inconsistent counts, empty and all-unplayed libraries, totals exceeding `Int64`, 20,000-row aggregation and cancellation. `InsightsViewModelTests` covers progressive snapshot changes, clearing stale metrics, private and failed outcomes, partial and empty data, and independence from Games filtering without additional service calls.
