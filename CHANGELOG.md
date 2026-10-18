# Changelog

## 1.0.0 - 2026-10-18

- Completed the independent Steam Profile Explorer release: Summary, Games/recent, Friends, lazy Achievements, paginated Inventory, arbitrary Profile A/B comparison, Mutual Games and Library Pulse.
- Added explicit CSV export of filtered Games/recent, Mutual Games and Inventory, including coverage, source timestamps, exact identifiers and unknown facts. Added spreadsheet formula protection, quoting and UTF-8 handling.
- Kept unknown mutual-game names empty in CSV instead of exporting an AppID display fallback.
- Fixed ambiguous single-profile/ban response handling and cancellation wrapped by LINQ sorting.
- Preserved Games/Friends selection across sorting and Pulse selection across language changes.
- Coalesced concurrent matching thumbnail requests through bounded gates and existing caches while retaining independent bitmap ownership.
- Unified theme accents and selected-row surfaces; added visible version and clarified configuration/export guidance. English/Ukrainian resources have 529 matched entries.
- Fixed PowerShell 7 absent-versus-empty environment restoration and strengthened release path/payload guards.
- Release build: zero warnings/errors. Passed 513 unit tests and 311 HTTP integration tests; one Unix-only test intentionally skips on Windows. Real-view checks cover 210 page/layout configurations and actual language/image/accessibility behavior.
- Windows self-contained packages include .NET 10.0.13, MIT license and dependency notices. Installer, upgrade, local configuration and final package evidence is recorded in docs/RELEASE_VERIFICATION.md.
- Added portfolio README, screenshots, architecture diagram, contribution/security guidance and GitHub issue/PR templates.
- Signing, credentialed live Web API, audible Narrator, clean-machine and physical multi-monitor acceptance retain explicit boundaries; no claim of universal certification is made.

## 0.9.0 - 2026-10-18 (local configuration, live language and Library Pulse)

- Added bounded, literal-only local `.env` configuration for the canonical Steam API key and startup language, with explicit environment precedence and no parent-directory search.
- Excluded local environment files from Git, build outputs and source export; Windows ZIP/setup payload validation rejects environment files. Tests use only synthetic credentials, never the owner's local key.
- Added an accessible sidebar language button that switches English/Ukrainian immediately, preserving the control tree, inputs, profiles, comparison sides, filters, selected rows and OS number/date formatting.
- Added observable resource cells and stable localized sort options; fixed selection/icon regressions caused by presentation observers entering record equality.
- Added friend avatars and inventory item icons before selection. Only visible realized rows load images; hidden/recycled rows cancel, reject obsolete results and release their owned bitmaps.
- Added Library Pulse: seven playtime buckets, top played games, concentration and known never-played metrics derived from the existing library snapshot without more HTTP requests. Incomplete denominators remain explicit.
- Refined shell/navigation, empty/error panels and compact English/Ukrainian layouts. All seven resource modules contain 521 paired Ukrainian entries with exact keys/placeholders.
- Passed 470 unit tests and 301 fake-HTTP integration tests; one Unix FIFO case is intentionally skipped on Windows. Release build has zero warnings/errors.
- Passed actual language-button/image lifecycle checks, 168 English DPI/theme page checks and 42 populated Ukrainian layout checks.
- Verified native Windows UIA across seven pages, English/Ukrainian names/status changes and preserved input. Automated UIA does not establish audible Narrator output.
- Fresh-key live Web API, spoken Narrator, a clean Windows machine, physical multi-monitor behavior and trusted signing remain explicit external acceptance work.

## 0.8.0 - 2026-10-17 (accessibility, Ukrainian UI and verified upgrades)

- Suppressed hidden-page live regions, added semantic headings/current-page status and fixed achievement sort/status overlap.
- Added 470 Ukrainian strings, explicit startup UI culture and isolated culture-selection tests.
- Added actual peer-event assertions and 36 populated Ukrainian layout checks alongside the 144 DPI page checks.
- Added installer mutex, numeric newer/ambiguous-version guards and conservative hash-verified managed-file retirement.
- Verified actual 0.7→0.8 upgrade, all 288 payload hashes, selected desktop task, reinstall and uninstall/user-file preservation.
- Verified retirement using a separate test-only payload; it is never the distributed app.
- Added owner-selected MIT license to source and binary packages and versioned the production User-Agent.
- Added opt-in aggregate-only profile diagnostics; verified its no-key/zero-network and argument paths.
- Passed 723 unit/integration tests and full headless checks. Native control was stopped with Esc; audible Narrator/new native UIA, fresh-key live and clean-machine acceptance remain unclaimed.

## 0.7.0 - 2026-10-17 (DPI, accessibility and installer)

- Added PerMonitorV2 manifest, screen work-area startup sizing, compact 840x480 minimum and scrollable navigation/filter budgets.
- Preserved virtualized viewports at 100/125/150/200% logical layouts; added24 theme/display configurations and144 page assertions.
- Added app high-contrast palette/platform preference response, visible keyboard focus, stable automation IDs and status live metadata.
- Verified native Windows UIA edit/value/focus/navigation and actual DPI-awareness context without global desktop changes.
- Added branded icons, reproducible per-user Inno setup, Start menu/optional desktop shortcuts, registration and uninstaller.
- Verified isolated install, native startup/UIA, same-version reinstall, uninstall and unmanaged-file preservation.
- Passed 717 unit/integration tests and the full real-view suite. Signing, clean-machine, spoken screen-reader and physical multi-monitor checks remain open.

## 0.6.0 - 2026-10-17 (live source and Windows release hardening)

- Verified real public Steam inventories and CDN responses using production services; recorded
  positive pagination/amount evidence and honest inaccessible PUBG/Rust outcomes.
- Added an explicit opt-in LiveChecks tool with sequential bounded requests and aggregate-only reports.
- Improved page/global search shortcuts, Escape routing, Games/Friends copy, Settings reachability
  and automation names across all six virtualized lists.
- Hardened avatar URL/decode/cache/disposal bounds and disabled cookies on the Web API transport.
- Propagated native application failure exit codes; added positive and intentional failure startup checks.
- Added reproducible self-contained .NET 10.0.13 Windows packaging, dependency manifest and retained notices.
- Passed 717 unit/integration tests and real-view UI checks. Credentialed live Web API, clean-machine,
  broader DPI/accessibility and external signed distribution remain explicit acceptance work.

## 0.5.0 - 2026-10-17 (inventory milestone)

- Added explicit read-only Inventory loading for CS2, TF2, Dota 2, PUBG, Rust and Steam Community,
  with editable validated numeric contexts and no login/cookie/publisher-key dependency.
- Added validated cursor pagination, repeated-cursor protection and explicit partial coverage on interruption.
- Joined descriptions by exact classid + instanceid, retaining asset identity, unsigned amount and unknown metadata.
- Added full virtualized results, debounced text/ID search, stable name/type/amount/asset sorting,
  known marketable/tradable filters, selected details and selected-only async icon loading.
- Distinguished explicit Empty, inaccessible/Unavailable, RateLimited, Failed, Loading and Ready outcomes;
  partial sources retain loaded rows but never establish global unit totals.
- Added exact decimal quantity aggregation and bounded memory cache, cancellation and stale-response guards.
- Added domain, fake-HTTP, lifecycle and synthetic UI scenarios; executed results are recorded in
  docs/INVENTORY_VERIFICATION.md. Live Steam and wider native release checks remain open.

## 0.4.0 - 2026-10-17 (achievements milestone)

- Added selected-game lazy achievement loading through validated player/schema Steam methods.
- Added explicit NoAchievements, Private, Unavailable, Failed, Loading and Ready outcomes; omitted
  or inconsistent information does not become zero progress.
- Added virtualized game/detail lists, text/status filters, stable sorting and optional unlock dates.
- Added selected-game progress and separately defined weighted/mean completion with checked-subset coverage.
- Added bounded independent schema/player-result caches, deduplication, cancellation and stale-write guards.
- Retained only selected full details in the ViewModel, with compact status/count snapshots for checked games.
- Added domain, fake-HTTP, lifecycle and synthetic UI regression scenarios; executed results are recorded
  in docs/ACHIEVEMENTS_VERIFICATION.md.
- Inventory remains planned; full feature parity is not yet complete.

## 0.3.0 - 2026-10-17 (friends milestone)

- Added validated GetFriendList and GetPlayerSummaries mapping with batches of at most 100 IDs.
- Retained every validated friend ID on partial enrichment; absent summaries remain unknown.
- Added a bounded 3-minute Friends memory cache, same-request deduplication and observer cancellation.
- Added virtualized Friends, debounced search, presence/name/date sorting and online-only filtering.
- Added selected-friend mini-profiles with cancellable avatar loading, clipboard and safe Steam links.
- Added Explorer/Compare A/B navigation without replacing the other comparison side.
- Added test cases for batching, cache races, missing data, filters, stale selection/avatar and navigation.
- Added the Friends verification report; final executed checks are recorded there separately.
- Achievements and Inventory remain planned; full feature parity is not yet complete.

## 0.2.0 - 2026-10-17 (library and comparison milestone)

- Added validated owned/recent Steam APIs, independent TTLs and a bounded deduplicated library cache.
- Added virtualized Games/recent, search/hour filters/sorting, clipboard and Store actions.
- Added independent Profile A/B and complete-library Mutual Games with both players' hours.
- Added cancellation, stale-state protection and strict missing/private/empty source semantics.
- Added domain/ViewModel/HTTP tests and separate headless UI assertions with labeled fixture screenshots.
- Friends, Achievements and Inventory remain planned; full feature parity is not yet complete.

## 0.1.0 - 2026-10-16 (first vertical slice)

- Audited six legacy Python modules and the available 52-page design document.
- Added independent .NET 10 / Avalonia profile explorer with validated identifiers.
- Added safe Steam client, independent summary enrichment, cancellation and typed availability.
- Added fake-HTTP integration tests and domain tests without credentials or production fixtures.
- Documented migration mapping, security, remaining parity work and release gates.

Historical entries record their milestone scope. The current delivery is a tested local release; external acceptance and trusted signing remain explicit in NEXT_STEPS.md.
