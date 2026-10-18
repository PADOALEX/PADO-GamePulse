# Friends milestone verification

Release milestone: 0.3.0, 2026-10-17. Implemented scope: Friends search/sorting, selected mini-profiles,
Explorer navigation and either comparison side. Achievements and Inventory remain unimplemented.
The earlier [library/comparison report](PHASE_C_VERIFICATION.md) is retained as historical evidence.

## Final execution record

Executed locally on 2026-10-17. Results below come from actual command output and TRX counters.
Synthetic UI fixtures and fake HTTP are isolated to test projects.

| Check | Actual result |
| --- | --- |
| SDK/runtime and host | Windows x64 10.0.26300; SDK 10.0.102; runtime 10.0.2; Avalonia 12.1.4 |
| Restore | `dotnet restore PADO.GamePulse.sln --disable-parallel -m:1`: exit 0; all projects restored/up to date |
| Release build | `dotnet build PADO.GamePulse.sln -c Release --no-restore -m:1`: exit 0, 0 warnings, 0 errors |
| Unit tests | 177 passed, 0 failed, 0 skipped |
| Fake-HTTP integration tests | 129 passed, 0 failed, 0 skipped |
| Headless UI assertions | Exit 0: full directory, last row, name/ID/online filters, known/unknown mini-profile, independent scroll, real Explorer/A/B button events, themes, 980 x 680 content viewport |
| Native no-key startup | Published DLL dark/light and EXE: exit 0 without a key; native setup screenshots inspected |
| Windows x64 publish/ZIP | `dotnet publish ... -r win-x64 --self-contained false`: exit 0; EXE product version 0.3.0; portable ZIP integrity and extracted EXE startup checked |
| Screenshots | [Dark](screenshots/friends-test-fixtures.png), [light](screenshots/friends-light-test-fixtures.png), [compact](screenshots/friends-minimum-test-fixtures.png), [compact details](screenshots/friends-minimum-details-test-fixtures.png); all synthetic and visibly labeled. [Native setup](screenshots/setup-windows.png) contains no profile data |
| Synthetic performance | 12,000 friends; 7/7 realized rows at start/end; last row reachable. 10 filter/sort passes: 202.153 ms, 4,016,360 bytes allocated on measured thread. [Raw observations](performance/friends-ui-check-results.json) |

No credentialed live Steam integration was performed. Do not replace that statement with a claim
based on fake HTTP or synthetic headless fixtures.

## Implementation boundary

- Production uses fixed Steam HTTPS endpoints, owner-provided keys and the existing secret-safe HTTP
  client. No demo-data switch, database, Steam login or mandatory server was added.
- Friend relationships and optional profile summaries are separate. Missing enrichment retains each
  validated ID, reports partial coverage and leaves presence/details unknown.
- Summary batches contain at most 100 distinct IDs; two parallel batches is an application scheduling
  cap, not an asserted Steam-wide request rate. The shared HTTP gate/retry/Retry-After rules still apply.
- A separate 3-minute complete-result cache has 30,000-row and estimated 16 MiB budgets. Identical reads
  share transport, final-observer cancellation stops it, and refresh prevents stale cache overwrites.
- Query/name/ID, online-only and presence/name/date sorts operate on the full directory. The UI list
  virtualizes rows; only the selected mini-profile requests an optional avatar.
- Explorer opens the selected independent account. Compare A/B replaces only its addressed side,
  retaining the other side's loaded identity, facts, library and active lookup.

## Executed regression matrix

All 306 unit/integration cases passed. The separate headless assertion executable also exited 0.
Timing/allocation values describe this local synthetic run; no live latency, frame-rate or speedup claim is made.

| Area | Covered cases |
| --- | --- |
| HTTP mapping | Explicit empty vs omitted/malformed list; 401/403/404/429/500; invalid IDs/duplicates/relationships; missing summaries; invalid JSON; payload bounds; cancellation |
| Summary batching | More than 100 friends; each batch at most 100; bounded parallelism; failed/omitted batch members retain IDs and unknown details |
| Cache | Hit/miss/expiry; force refresh; row/byte limits; partial result not cached; same-key deduplication; observer cancellation; stale writers |
| Queries | Case-insensitive name and SteamID search; known online-only; all sort orders; missing dates/presence last; complete large source |
| ViewModel lifecycle | Loading/Empty/Private/Unavailable/Error; missing key; profile replacement; uncooperative cancellation; stale filter/selection/avatar; dispose |
| Navigation | Explorer from Friends; A/B preserve the other side; untouched in-flight lookup stays active; invalid IDs cause no request; action event wiring; stale Explorer result rejected |
| UI | Full synthetic friend source; bounded realized containers; scroll to last row; filters/sorting; known/unknown mini-profile; Explorer/Compare actions; dark/light; compact window |
| Privacy | No raw key-bearing URL/error in UI/logging; HTTPS and fixed-host requests; existing trace regression retained |

## Reproduction

Run from the solution directory with a supported .NET 10 SDK. None of these test commands needs a real
Steam key. The additional UI checker is an executable assertion suite, not a test-discovery project.

```powershell
dotnet restore PADO.GamePulse.sln --disable-parallel -m:1
dotnet build PADO.GamePulse.sln -c Release --no-restore -m:1
dotnet test PADO.GamePulse.sln -c Release --no-build -m:1 --logger trx
dotnet run --project tests/GamePulse.UiChecks -c Release --no-build -- artifacts/ui-checks
dotnet publish src/GamePulse.Desktop/GamePulse.Desktop.csproj -c Release -r win-x64 --self-contained false -m:1 -o ../PADO.GamePulse-win-x64
dotnet ../PADO.GamePulse-win-x64/GamePulse.Desktop.dll --smoke-test --smoke-output setup.png
```

Extract the whole portable Windows ZIP before starting GamePulse.Desktop.exe. A supported .NET 10
runtime is required. Signing, a native installer and clean-machine validation are separate release gates.

## Source limits and unperformed checks

The [official ISteamUser reference](https://partner.steamgames.com/doc/webapi/ISteamUser) documents the
100-ID summary limit and private friend lists returning HTTP 401. An authorization failure alone does
not distinguish privacy from key/access problems. Profile visibility is not friend-list visibility;
missing summaries never establish an offline/private/nonexistent account. Explicitly absent information
remains unknown. Method context restrictions and current Valve terms still apply to distribution/use.

Test fixtures/screenshots are explicitly synthetic and are confined to test projects. No historical
relationship archive, telemetry or persistent friend storage was added. Original Python files and keys
remain unchanged; a key distributed in the old archive should be revoked/rotated.

Unverified here: credentialed live public/private/access-error matrix, actual Steam latency or scroll
FPS, native Linux/macOS, broad DPI/accessibility coverage, signed installers and hosted CI execution.
Use the owner's fresh permitted local key for a separate live verification session; never put that key
in command arguments, logs, screenshots or artifacts. Next implementation milestone: Achievements,
then Inventory, following [NEXT_STEPS.md](../NEXT_STEPS.md).
