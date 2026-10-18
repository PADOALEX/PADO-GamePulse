# Inventory milestone verification — 0.5.0

Date: 2026-10-17. This report concerns the read-only Inventory milestone, not a fully verified public release.

## Implemented behavior

- Explicit loading for CS2 (730), TF2 (440), Dota 2 (570), PUBG (578080), Rust (252490) and Steam
  Community (753); editable canonical positive numeric context. Defaults 2 / Community 6 are not
  guaranteed valid for every profile and can be changed before loading.
- Cursor pagination with exact classid + instanceid description joins, distinct asset identities and
  unsigned amount. Missing or conflicting metadata remains visible and unknown.
- Full virtualized results, search across name/type/asset/class/instance ID, stable name/type/amount/
  numeric asset sorts, known marketable/tradable filters and selected-item details.
- Async selected-only icon loading, bounded caches, cancellation and obsolete-result suppression.
- Explicit Empty, inaccessible/Unavailable, RateLimited, Failed, Loading and Ready outcomes.
  Interrupted pagination retains valid loaded rows with incomplete coverage and its cause.
- Separate asset and loaded-unit counts. Decimal aggregation is exact beyond UInt64 totals; partial
  snapshots have no global unit total. Known-true marketable/tradable counts describe loaded subsets.

## Source and local resource policies

Primary implementation evidence was fetched read-only from
[Steam's inventory frontend JavaScript](https://community.fastly.steamstatic.com/public/javascript/economy_v2.js)
on 2026-10-17. The reviewed source constructs `/inventory/{id}/{app}/{context}`, uses more_items,
last_assetid/start_assetid, joins classid + instanceid and reads total_inventory_count. This is an
unstable Community implementation detail, not a guaranteed published API contract. The official
[IInventoryService](https://partner.steamgames.com/doc/webapi/IInventoryService) is a publisher service
and is not the API used here. No live inventory JSON or protected data was fetched.

The fixed-host HTTPS Community client sends no Web API key or cookie, disables redirects and retains
normal certificate validation. Default local policies: one concurrent request (configurable up to two),
2,000 assets per page, 8 MiB per-page wire bound, at most three retry attempts and respected Retry-After
up to 30 seconds. These are local resource choices, not official Steam rate guarantees.

Default configurable total-load safety bounds are 250,000 assets, 64 MiB received and 1,000 pages.
If a bound is reached, prior rows remain explicitly incomplete rather than being labeled complete.
Complete Ready/Empty snapshots alone can enter the separate session-only 2-minute cache, bounded by
256 entries, 30,000 rows and estimated 16 MiB. Missing metadata may remain cached as explicitly unknown;
partial pagination and failures are not cached. Oversized complete snapshots remain viewable intact.

## Verification record

Executed locally on Windows x64 10.0.26300, .NET SDK 10.0.102 / runtime 10.0.2, Avalonia 12.1.4.
The following record comes from actual command output, final TRX counters and view assertions.

| Check | Final execution evidence |
| --- | --- |
| Restore/build | `dotnet restore ... --disable-parallel -m:1` and Release build: exit 0; final build has 0 errors / 0 warnings |
| Unit/integration tests | 395 unit + 301 fake-HTTP integration = 696 passed, 0 failed, 0 skipped |
| Headless UI assertions | Exit 0: 12,000 assets / 36,000 units, only 7 realized rows at the measured full-size point; last row reached; no requests before explicit load; filters/sorts/selection/details, partial/empty/denied/rate-limit/error, context change, cancel button, themes and 980 x 680 content layout |
| Native no-key startup | Published DLL dark/light and EXE: exit 0; native setup screenshots inspected; EXE product version 0.5.0 |
| Windows ZIP extraction/startup | Windows x64 framework-dependent publish: exit 0; 0.5.0 ZIP CRC/SHA256/path validation and extracted EXE startup checked |
| Source/ZIP secret exclusions and integrity | Local Markdown links resolve; archive CRC checks pass; .env (except empty .env.example), .venv, IDE files, bin/obj, tests binaries and original archive excluded from source; production deps contain no Headless test assembly |

Domain scenarios cover strict SteamID/context validation, all six app defaults, name/type/all-ID
searches, unknown flag filters, numeric asset ordering, 12,000 untruncated rows, partial totals and
100,000 quantities of ulong.MaxValue without UInt64 overflow. Additional fake-HTTP and desktop
lifecycle/UI scenarios are listed with the final execution evidence rather than assumed live behavior.

## Reproduction

From the solution directory with a supported .NET 10 SDK:

```powershell
dotnet restore PADO.GamePulse.sln --disable-parallel -m:1
dotnet build PADO.GamePulse.sln -c Release --no-restore -m:1
dotnet test PADO.GamePulse.sln -c Release --no-build -m:1
dotnet run --project tests/GamePulse.UiChecks -c Release --no-build -- artifacts/ui-checks
dotnet run --project src/GamePulse.Desktop -c Release --no-build
dotnet publish src/GamePulse.Desktop/GamePulse.Desktop.csproj -c Release -r win-x64 --self-contained false -m:1 -o ../PADO.GamePulse-win-x64
```

The Windows folder is framework-dependent and requires a supported .NET 10 runtime. Extract the
whole ZIP before starting GamePulse.Desktop.exe. It is not a signed installer.

## Unverified release gates

No credentialed live Steam integration was performed, and no public inventory JSON was fetched as a
live behavior test. An owner must verify permitted current public/denied/rate-limited/partial sources,
the six app contexts, real metadata and large pagination with a fresh local key where protected methods
need it. Do not use a key distributed with the legacy archive. Broader native Windows DPI/accessibility,
clean-machine/runtime checks and native Linux/macOS/hosted CI execution remain open. Synthetic UI
timings are not live API latency, rendered scrolling FPS or a claimed speedup.

The original Python archive remains unchanged; no legacy key was read/copied. No database, account,
server, cookie automation or production fixture was added. Owner licensing/signing decisions remain open.

## UI observations and important files

[Dark](screenshots/inventory-test-fixtures.png), [light](screenshots/inventory-light-test-fixtures.png),
[compact](screenshots/inventory-minimum-test-fixtures.png), [compact details](screenshots/inventory-minimum-details-test-fixtures.png),
[partial](screenshots/inventory-partial-test-fixtures.png), [unknown metadata](screenshots/inventory-missing-metadata-test-fixtures.png)
are actual rendered views with visibly labeled synthetic fixtures. [Native setup](screenshots/setup-windows.png)
contains no Steam profile data.

Ten synthetic full-source filter/sort passes: 44.269 ms;
2,321,080 bytes allocated on the measured thread. These local observations
are not live Steam latency or scrolling-FPS benchmarks. [Raw record](performance/inventory-ui-check-results.json).

Selected-icon tests in the real headless renderer verify independent caller-owned bitmaps, encoded-byte
cache hits, 30-minute expiry, 48-entry eviction and rejected oversized dimensions. Images use one fixed
HTTPS economy-image CDN, no cookies, no redirects or query credentials; 2 MiB wire, 12 MiB cache,
two concurrent loads, 10-second deadline and bounded 256-pixel decoding of validated source dimensions.
The source image must be at most 8192 pixels per axis and 16 million pixels total.

Typed StopReason distinguishes configured page/size bounds, changed counts, inconsistent pagination and
omitted totals. HTTP failures retain a separate interruption state. UI text never displays arbitrary
transport messages. Background statistics/filtering and generation guards cover profile/source/filter/
icon changes. Compact sidebar spacing keeps session recents scrollable after adding the sixth tool.

| File | Responsibility |
| --- | --- |
| src/GamePulse.Core/Inventory.cs | Source/item/snapshot contracts, six editable context defaults, typed stop causes |
| src/GamePulse.Core/InventoryQuery.cs | Validation, exact quantities, filters and numeric ordering |
| src/GamePulse.Infrastructure/SteamInventoryClient.cs | Key-free bounded Community transport/DTO mapping |
| src/GamePulse.Infrastructure/SteamInventoryService.cs | Pagination, joins, partial snapshots, cache/progress/cancellation |
| src/GamePulse.Desktop/ViewModels/InventoryViewModel.cs | Source/selection state and background work |
| src/GamePulse.Desktop/Views/InventoryView.axaml | Virtualized list and scrolling details |
| src/GamePulse.Desktop/Services/InventoryIconService.cs | Bounded selected-item image transport/decode |
| tests/GamePulse.UiChecks/Program.cs | Real-view assertions with test-only fixtures |

All requested feature areas now have an implementation. Credentialed/live source verification, broader
native OS/DPI/accessibility checks, signing/installer and the owner's license decision remain release gates.
See [NEXT_STEPS](../NEXT_STEPS.md); no full public-release/MVP verification is claimed here.
