# SteamChecker to PADO GamePulse migration map

The product remains an independent explorer of arbitrary Steam profiles. No Steam sign-in, personal account registration, required database, server, or production demo profiles are introduced. The Python archive remains the unchanged comparison baseline outside the new solution. Source behavior is documented in [AUDIT.md](AUDIT.md).

## Architecture decisions

- `GamePulse.Core`: immutable profile models, input validation/normalization, explicit result states and search-generation rules. No Avalonia or HTTP dependencies.
- `GamePulse.Infrastructure`: HTTPS Steam client, API DTOs/mapping, environment options, bounded retries/cancellation, TTL cache and logging that cannot include key-bearing request URLs.
- `GamePulse.Desktop`: Avalonia MVVM, composition with dependency injection, UI-thread publication, profile summary and setup/error states. Resource strings keep future localization possible.
- Tests use fake HTTP only in the test layer. Production always obtains supported data from Steam or explains its absence.
- The first delivery is a runnable Summary vertical slice. The tables below are migration destinations, not a claim that every destination is implemented. The root README and NEXT_STEPS record current shipped scope.

## Function-to-component mapping

The names in the destination column describe responsibility; concrete source names may combine closely related services to keep the first slice small.

| Python function/state | .NET responsibility | Phase |
| --- | --- | --- |
| `steam_api.get_steam_id`; `api_handler.get_steam_id` | Core `ProfileInput` validation plus Steam client's `ResolveVanityURL`; profile lookup service resolves only vanity slug, never a global display name. | B |
| `event_handlers.search_player` | Desktop search command/ViewModel; Core `SearchSession` invalidates previous generation; cancel prior search; render Summary before optional modules. | B |
| `steam_api.get_player_summary`; `api_handler.get_player_summary` | Infrastructure summary DTO/client mapper and profile lookup; documented batch size for later Friends page. | B/C |
| `steam_api.get_player_bans`; `api_handler.get_player_bans` | Independent nullable bans result; VAC flag/count, game-ban count, community and economy facts when returned. Failure is unavailable, never clean. | B |
| `steam_api.get_steam_level`; `api_handler.get_steam_level` | Independent level result; `N/A` for unavailable level. | B |
| `event_handlers.update_summary_tab` | Summary view/ViewModel: avatar, display name, normalized ID, safe Steam link, all persona states, creation time and independent level/ban availability. | B |
| `ui_helpers.show_loading_animation`, nested `step`; `stop_loading_animation` | Per-section busy state and cancellation-aware commands. No shared animation flag. | B |
| `ui_helpers.update_search_history`; `main_app.show_search_history`, nested `dbl` | Bounded session-only recent queries and repeat-search command. Optional JSON persistence remains opt-in future work. | B |
| `ui_helpers.clear_all_tabs` | Reset ViewModel state atomically when search generation changes; cancel work before releasing images/collections. | B |
| `main_app.show_context_menu` | Bound clipboard/external-link commands with stable SteamID/AppID values; no lookup by nonunique game name. | B/C |
| `ui_helpers.copy_to_clipboard`; `open_steam_store` | Desktop platform adapters; HTTPS Steam profile/store links and safe ID commands. | B/C |
| `ui_helpers.toggle_theme`; `default_bg` | Avalonia theme variants and shared resource dictionary. | B/D |
| `main_app.show_hotkeys_help`; module-level widget setup/bindings | Avalonia `MainWindow`/composition root, Enter search, focus shortcuts, accessible labels, resizable layout. | B/D |
| `api_handler.fetch_image`; `get_avatar` | HTTPS image service with destination validation, bounded bytes/pixels/cache and off-thread decode; canceled requests cannot populate a new profile. | B/D |
| `steam_api.get_owned_games`; `api_handler.get_owned_games`; `event_handlers.update_games_tab` | Library service and virtualized Games view: complete returned library, minutes retained as source unit, total/top metrics, search/sort/hour filters. | C |
| `steam_api.get_recently_played_games`; `api_handler.get_recently_played_games` | Independent recent-games result; source-backed last-two-week metrics. | C |
| `steam_api.get_player_cs_stats`; `api_handler.get_player_cs_stats` | Optional per-game statistics adapter/section; absence explained rather than removing legacy capability silently. Current endpoint/game availability needs verification. | C, conditional |
| `steam_api.get_prices_for_apps`; `api_handler.get_prices_for_apps` | Optional catalogue estimate provider only after endpoint/terms verification; coverage, region, currency, checked time and limitations required. Never purchase spending. | Deferred/conditional |
| `steam_api.get_player_achievements`; `api_handler.get_player_achievements` | Lazy achievement service with explicit ready/no-achievements/private/unavailable/failed outcomes; selected game prioritized. | C |
| `steam_api.get_game_schema`; `api_handler.get_game_schema` | Longer-lived game-schema metadata cache; merge schema `name` with player `apiname`, preserve unknown metadata and unlock time. | C |
| `event_handlers.update_achievements_tab`; `on_achievement_game_select` | Achievement game list/detail ViewModels; bounded user-triggered aggregation, progress/cancel; separately named weighted and per-game completion. | C |
| `steam_api.get_friend_list`; `api_handler.get_friend_list`; `event_handlers.update_friends_tab` | Friends service, chunked summaries, explicit list availability, search/sort, full status mapping. | C |
| `main_app.show_mini_profile`; `hide_mini_profile` | Bound lightweight tooltip using loaded friend records and shared async avatar cache; no network operation inside pointer callback. | C/D |
| `event_handlers.on_friend_select` | Selected friend's existing summary; optional compare-side assignment. Loading Profile B also accepts any arbitrary valid profile input. | C |
| `main_app.open_comparison_window`, nested `col` | Profile comparison service/ViewModel with independently editable Profile A/B; unavailable values remain N/A. | C |
| `main_app.find_mutual_games` | Core comparison rules plus virtualized shared-games list keyed by AppID and each player's minutes; library availability checked before intersection. | C |
| `steam_api.INVENTORY_CONTEXTS`; `get_player_inventory`; `api_handler.get_player_inventory` | Inventory options/service for 730/440/570/578080/252490/753 and configurable context IDs; request each inventory only on demand. | C |
| `steam_api._make_session` | Shared configured `HttpClient` lifetime; truthful application user agent, TLS validation, no browser impersonation/privacy bypass. | B/C |
| `steam_api._fetch_inventory_pages` | Inventory transport/paginator: actual continuation contract, unique cursor guard, independent finite retries/pages, explicit rate limit/private/empty/partial outcomes and cancellation. | C |
| `steam_api._build_description_index`; `_resolve_desc` | Testable exact `(classid,instanceid)` mapper preserving context, asset identity, unknown metadata and integer stack amount. | C |
| `event_handlers.update_inventory_tab`, nested `finalize` | Virtualized/incremental inventory view; all fetched assets accessible; responsive columns; separate asset-row and quantity summaries. | C/D |
| `api_handler.get_item_icon_bytes`; nested `make_photo`, `load_icon_async`, `task`, `apply`, `set_icon` | Bounded deduplicated image service, off-thread decode, UI-thread publication only for live request generation, deterministic disposal/cancellation. | C/D |
| `ui_helpers.install_global_mousewheel`, nested `select_canvas`, `on_wheel`; `register_scroll_canvas`, nested `on_destroy` | Native Avalonia scroll viewers and virtualizing panels, using ordinary focus/pointer routing and view lifetime. | C/D |
| `config.py` global current/selected profiles, lists, caches, flags, executor, canvases | ViewModel-owned immutable snapshots, injected bounded caches/options/services, explicit lifetime-owned cancellation. No global mutable application state. | B/C/D |

## Semantic corrections to preserve in every module

| Source ambiguity | Required migration rule |
| --- | --- |
| Empty/null/exception used interchangeably | Typed outcomes keep Loading, Ready, Empty, Private, Unavailable, NotFound, InvalidInput, RateLimited and Failed distinct as evidence allows. Do not infer privacy solely from an empty body. |
| Missing ban data becomes false or crashes | Nullable ban values plus independent failure state; never infer integrity, fairness, criminality or trustworthiness. |
| Empty game response treated as zero | A library is empty only when source evidence explicitly supports an accessible empty library. Missing game list is unavailable/partial. |
| Empty AppID intersection regardless of access | Intersect only two accessible libraries; otherwise comparison is unavailable. |
| Any nonnumeric input sent as vanity | Accept normalized SteamID64 and verified Steam community profile/vanity URLs; reject unsupported hosts/credentials/ports/routes and explain nonunique display names. |
| “Average achievement percent” | Weighted = all unlocked / all available achievements in successfully loaded games; per-game average = average of those games' completion. Show loaded coverage; do not imply complete-library coverage. |
| Inventory totals use `len(items)` | Expose asset count and summed quantity separately; stack-aware marketable/tradable counts must state their unit. |
| Price sum starts at zero after errors | Missing is unknown; optional estimate can be partial only when its coverage and provenance are explicit. |

## Delivery sequence and acceptance gates

1. **A: Audit/design.** All six files read and statically parsed; reference PDF read; verified findings and function map documented. Check actual SDK and stable package compatibility against official sources.
2. **B: Summary vertical slice.** Buildable layered Avalonia solution, no-key setup screen, real HTTPS Steam summary lookup, validated arbitrary-profile inputs, session history, safe error states, independent level/bans, cancellation/stale-result tests, README and automated tests. No key from the legacy archive may be used.
3. **C1: Library + compare.** Full available library/recent list, virtualization/filtering; independently editable Profile A/B; null-safe metrics and AppID intersection. Validate private versus accessible empty libraries and partial responses before enabling aggregate insights.
4. **C2: Friends + achievements.** Documented batching; friend mini-profiles; lazy selected-game achievements/schema; coverage-aware aggregation, bounded work, cancellation and tests.
5. **C3: Inventory.** Verify public endpoint limitations; configurable contexts; cursor/amount/pair-join tests; no hidden truncation; explicit 403/429/partial/empty outcomes. Price estimation stays disabled until its contract is supportable.
6. **D: Measured UX/performance.** Large fake datasets belong exclusively in test/profiling layers. Measure first meaningful content, full completion, request counts, retries, memory and scrolling separately; verify DPI/themes/keyboards and image-cache limits. No claimed speedup without recorded baseline/results.
7. **E: Release preparation.** Real permitted screenshots or explicitly labeled test examples; README in English with concise Ukrainian setup; CI; changelog; security/API limitations; appropriate publish packages where built and tested. License choice remains the owner's decision.

A runnable Phase B is a stable intermediate delivery, not completed feature parity. Shipped functionality and deferred work must be stated explicitly in root documentation.
