# Phase C library/comparison verification

Release milestone: 0.2.0, 2026-10-17. This completes the requested Games/recent + Compare/Mutual scope.
Friends, Achievements and Inventory remain outside this milestone and are not represented as complete.

## Actual checks

- .NET 10 SDK 10.0.102 / runtime 10.0.2 on Windows x64; existing serviced-LTS target retained.
- Restore and Release solution build succeeded; final build has 0 errors / 0 warnings.
- 125 unit tests and 81 fake-HTTP integration tests passed, with no failures/skips.
- UI assertions load the real views with Avalonia Headless and test-only synthetic services.
- 12,000 Games rows and 6,000 Mutual rows stay in their full sources. Only visible containers are realized;
  scrolling reaches the last row. Navigation shows one page, not overlaid views.
- Name/AppID/hour filters, recent switching, private-side N/A, 980 x 680 layout, expanded fact panels and
  dark/light themes are exercised. Screenshots contain an explicit test-fixture/not-live banner.
- The JSON in [performance/phasec-ui-check-results.json](performance/phasec-ui-check-results.json) records actual local timings, allocations and
  realized counts. These are synthetic domain/UI observations, not live Steam/FPS benchmarks.
- Native no-key startup and framework-dependent Windows publish are verified separately.

## Meaningful regression cases

Input/HTTPS/log privacy from Phase B remain covered. New tests include missing/private/unavailable vs
explicit empty libraries; invalid JSON/counts/AppIDs/duplicates; missing minutes; payloads over 2 MiB;
recent completeness; independent cache expiry/force/row+byte budgets; dedup and observer cancellation;
stale cache completions; both arbitrary sides; unknown aggregates; empty vs unavailable intersections;
10k/20k filtering/intersection; replacement searches; and transports ignoring cancellation.

Review found and repaired: owned-only Never played leaking into Recent; cancelled completed requests
remaining Loading; cancellation waiting on uncooperative transports; stale comparison coordinators;
feature DataContext visibility overlays; overlapping metric cards; compact viewport starvation; and
per-sort-comparison delegate allocations. Regression/visual checks verify the final behavior.

## Reproduction

From the solution directory:

```powershell
dotnet restore PADO.GamePulse.sln --disable-parallel --force --no-http-cache -m:1
dotnet build PADO.GamePulse.sln -c Release --no-restore -m:1
dotnet test PADO.GamePulse.sln -c Release --no-build -m:1 --logger trx
dotnet run --project tests/GamePulse.UiChecks -c Release --no-build -- artifacts/ui-checks
dotnet publish src/GamePulse.Desktop/GamePulse.Desktop.csproj -c Release -r win-x64 --self-contained false -m:1 -o ../PADO.GamePulse-win-x64
dotnet ../PADO.GamePulse-win-x64/GamePulse.Desktop.dll --smoke-test --smoke-output setup.png
```

The execution sandbox required workspace-local CLI/NuGet caches and one MSBuild node. A fresh NuGet
restore after network access succeeded; no vulnerability warning was suppressed. Hosted CI is configured
to run tests plus UiChecks on three OSs but no hosted run is claimed.

## Production boundary and limitations

All example datasets/services are confined to tests/GamePulse.UiChecks or test projects. The normal app
contains no demo-data switch. Its production services use Steam HTTPS APIs with permitted owner keys.
No key was read from the old archive, and no credentialed live Steam test was performed.
No actual Steam latency, native smooth-scroll FPS, broad DPI/accessibility matrix, clean-machine install,
Linux/macOS native run, signed installer or hosted CI result is claimed.

GetOwnedGames completeness is relative to the API-returned list/count and visibility, not all possible
entitlements. Missing data does not establish nonownership, a clean account, zero playtime or zero mutual
games. Official source reviewed: [IPlayerService](https://partner.steamgames.com/doc/webapi/IPlayerService).
