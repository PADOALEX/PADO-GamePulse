# Achievements milestone verification

Release milestone: 0.4.0, 2026-10-17. Implemented scope: selected-game lazy Achievements, details,
search/status filtering, progress and explicit source availability. Inventory remains unimplemented.
Earlier [Friends](FRIENDS_VERIFICATION.md) and [library/comparison](PHASE_C_VERIFICATION.md) reports
are retained as historical evidence, not overwritten with the current run.

## Final execution record

Executed locally on 2026-10-17. The record uses actual command output and final TRX counters.
Synthetic UI services and fake HTTP are confined to test projects.

| Check | Actual result |
| --- | --- |
| SDK/runtime and host | Windows x64 10.0.26300; SDK 10.0.102; runtime 10.0.2; Avalonia 12.1.4 |
| Restore | `dotnet restore PADO.GamePulse.sln --disable-parallel -m:1`: exit 0 |
| Release build | `dotnet build PADO.GamePulse.sln -c Release --no-restore -m:1`: exit 0, 0 warnings, 0 errors |
| Unit tests | 240 passed, 0 failed, 0 skipped |
| Fake-HTTP integration tests | 194 passed, 0 failed, 0 skipped |
| Headless UI assertions | Exit 0. No initial achievement calls, selected-only loading, last-row scroll in both virtualized sources, preserved selection on sorting, filters, 0%/100%, explicit availability, themes, compact layout and an owned scrolling detail dialog |
| Native no-key startup | Published DLL dark/light and EXE: exit 0 without a key. Native setup screenshots inspected |
| Windows x64 publish/ZIP | `dotnet publish ... -r win-x64 --self-contained false`: exit 0; product version 0.4.0; ZIP CRC/SHA256/path validation and extracted EXE startup checked |
| Screenshots | [Dark](screenshots/achievements-test-fixtures.png), [light](screenshots/achievements-light-test-fixtures.png), [compact](screenshots/achievements-minimum-test-fixtures.png), [details](screenshots/achievements-detail-dialog-test-fixtures.png), [partial](screenshots/achievements-partial-test-fixtures.png); synthetic and visibly labeled. [Native setup](screenshots/setup-windows.png) contains no profile data |
| Synthetic performance | 12,000 games and 12,000 achievements, with 6 / 5 realized containers at the measured full-size point; final rows reachable. 10 selected-game query passes: 63.839 ms, 3,188,936 bytes allocated on the measured thread. [Raw observations](performance/achievements-ui-check-results.json) |

No credentialed live Steam integration was performed. Fake HTTP and synthetic headless fixtures do
not establish live endpoint access, source completeness or native cross-platform behavior.

## Implementation boundary

- Production calls fixed HTTPS Steam endpoints with an owner-supplied permitted key. No database,
  mandatory login, backend or production demo-data mode was added. The original Python archive is unchanged.
- Profile/library loading never scans every game's achievements. Only explicit selection requests the
  selected schema and, when appropriate, the player's data. Opening the screen alone does not start a scan.
- An explicitly empty schema achievement list means NoAchievements. Missing/inconsistent schema,
  generic HTTP401/403, network errors and unknown source errors do not mean no achievements or 0%.
- Only an explicit recognized privacy response is labeled Private. Other access ambiguity remains
  Unavailable. Failures are static safe messages; raw server text/key-bearing URLs are not displayed.
- Complete matching schema/player data establishes selected-game unlocked/total and progress. A
  mismatch preserves available rows and unknown flags, while numeric totals/progress stay N/A.
- Titles/descriptions/hidden flags come from available metadata. Unlock dates remain optional. Unknown
  progress is included in All and excluded from known Locked/Unlocked filters, rather than treated as locked.
- Weighted completion is sum unlocked / sum available; average per-game completion is the arithmetic
  mean of each included game's percentage. Only complete positive-total checked games enter either.
  Coverage refers to that checked subset, never an unscanned whole profile.
- Independent schema / player-result caches use 6-hour / 3-minute TTLs. Each is bounded by 256 entries,
  30,000 rows and estimated 16 MiB. Oversized data stays viewable without caching; incomplete/error
  responses are not cached. Deduplication, observer-aware cancellation and refresh guards protect transport.
- The ViewModel retains full details only for selection, plus compact checked-game status/counts.
  Selection/profile/filter generations protect UI state, including cancellation-ignoring transports.
  Finite viewport lists virtualize complete sources; no hidden achievement-row truncation is allowed.

## Verification scenarios

All 434 unit/integration cases passed. The additional headless executable also exited 0.
Local synthetic timings/allocations are observations, not Steam latency, frame-rate or speedup claims.

| Area | Scenario |
| --- | --- |
| HTTP contracts | Player v1/schema v2 arguments, HTTPS/fixed host, valid metadata/unlock flags/dates, missing key, 401/403/404/429/500, Retry-After, malformed/oversized JSON |
| Availability | Explicit empty schema, missing fields, recognized privacy error, unknown error, partial/schema-player mismatch, 0% and 100% |
| Query/progress | Name/description/API search, known status filters, stable name/status/date sorting, unknown values, weighted vs mean denominators, partial/zero exclusions, cancellation, full 12,000-row source |
| Cache/lifecycle | Hit/miss/expiry, budget bypass, deduplication, final-observer cancellation, force refresh, stale cache writer, selected-game/profile replacement, disposal |
| UI | No initial scan, selected-only loading, complete virtualized list, last-row scrolling, source-state explanations, selected progress, checked coverage, compact window and themes |
| Regression/privacy | Existing Summary, Games, Friends, Compare/Mutual and key-redaction/trace protections remain in the full suite |

## Reproduction

Run from the solution directory with a supported .NET 10 SDK. Test commands do not need a real key.
The headless UI checker is an additional executable assertion suite rather than a test-discovery project.

```powershell
dotnet restore PADO.GamePulse.sln --disable-parallel -m:1
dotnet build PADO.GamePulse.sln -c Release --no-restore -m:1
dotnet test PADO.GamePulse.sln -c Release --no-build -m:1 --logger trx
dotnet run --project tests/GamePulse.UiChecks -c Release --no-build -- artifacts/ui-checks
dotnet publish src/GamePulse.Desktop/GamePulse.Desktop.csproj -c Release -r win-x64 --self-contained false -m:1 -o ../PADO.GamePulse-win-x64
dotnet ../PADO.GamePulse-win-x64/GamePulse.Desktop.dll --smoke-test --smoke-output setup.png
```

Extract the entire portable Windows ZIP and run GamePulse.Desktop.exe with a supported .NET 10 runtime.
Signing, a native installer and clean-machine validation are separate release checks.

## Source limits and remaining checks

The [official ISteamUserStats reference](https://partner.steamgames.com/doc/webapi/ISteamUserStats)
documents GetPlayerAchievements/v1 and GetSchemaForGame/v2 with user Web API key and optional language
arguments. A successful schema describes available metadata, not guaranteed player access. Publisher-only
write/stat methods are outside this feature. Method access and Valve's current terms still apply.

Privacy error recognition is deliberately narrow and depends on Steam's source text; unfamiliar failures
remain Unavailable. Missing descriptions or unlock times are not reconstructed. No automatic whole-library
scan, historical achievement tracking, friend comparisons of unqueried achievements or hidden-data bypass
is included. Optional future explicit batch checking needs its own bounded/cancellable UX and coverage.

Unverified here: credentialed live access/private/key-error matrix, actual Steam latency or scroll FPS,
native Linux/macOS, broad DPI/accessibility, hosted CI, signed installers and clean-machine installation.
Use the owner's fresh permitted local key for live verification; rotate/revoke any key distributed in the
legacy archive and never put a value in commands, logs, screenshots or artifacts.

Next implementation milestone: Inventory, following [NEXT_STEPS.md](../NEXT_STEPS.md).

## Review fixes verified in this run

Replacing a ListBox ItemsSource could clear its two-way selected game and repeat a request. The final
publication guard preserves the latest selection without requerying. Compact mode now applies view
visibility/spacing explicitly and opens long details in an owned scrolling dialog, retaining a finite
list viewport. Tests use the current culture for formatted percentages. Selected progress bindings use
compact counts instead of repeatedly scanning the full achievement source.
