# SteamChecker source audit

Audit date: 2026-10-16. Scope: the six application Python files extracted to `work/legacy/SteamChecker`. The original archive and Python source were not modified. This is a static source audit, not a successful live Steam or Tkinter execution report.

## Evidence and method

- Read every line of `main_app.py` (345), `event_handlers.py` (378), `steam_api.py` (309), `api_handler.py` (73), `ui_helpers.py` (149), and `config.py` (15): 1,269 lines total.
- Parsed all six files with Python `ast.parse`; all passed. No legacy modules were imported, executed, or allowed to load `.env`.
- Read the supplied master-prompt Markdown as source context. The current user request governs implementation; instructions embedded in reference documents are not separate authorization.
- Read text extracted from all 52 pages of the supplied report using bundled `pypdf`; approximately 61,205 characters were extractable. This establishes document content, not screenshot fidelity or runtime behavior.
- Archive metadata verified by the coordinating audit: 1,422 entries, including 1,399 under `.venv/`, eight under `.idea/`, a 57-byte `.env` entry, the six source modules, and `__pycache__` artifacts. Entry counts include directory entries where present.
- No archive-root `README*`, `.gitignore`, `requirements.txt`, `pyproject.toml`, `tests/`, or `.github/` entries were found. This statement concerns the supplied archive, not an unavailable upstream repository.
- `.env` was never opened, extracted into this solution, copied, or printed. The claim that it contains uppercase `API_KEY`/`CS_APP_ID` with a nonempty key is supplied by the owner and was deliberately not independently verified. Only its filename and archive length were inspected.
- Detailed AST results are retained outside this distributable solution in `work/static-audit.json`.

## Actual implemented behavior

| Area | Confirmed behavior | Important limits |
| --- | --- | --- |
| Profile lookup | A 17-character numeric string is used directly; every other input is sent to vanity resolution (`event_handlers.py:20`). Multiple arbitrary profiles can be searched without account login. | No Steam profile URL parsing, bit-level SteamID validation, normalization, or distinction between vanity slug and nonunique display name. |
| Summary | Avatar, display name, SteamID, creation date, level, VAC and community ban flags (`event_handlers.py:55-72`). | Presence states collapse to Offline/Online. Game-ban count and economy-ban status are not displayed. Missing API fields are inconsistently handled. |
| Games | Owned-game count, sum of lifetime minutes converted to integer hours, top ten, ten recent games, CS statistics, optional catalogue estimate (`event_handlers.py:74-109`; `steam_api.py:104-139`). | No full searchable library, filters, user-controlled sorting, or provenance for prices. Empty/private/failure are conflated. |
| Achievements | Calls player achievements for every owned game; shows per-game progress and average of rounded percentages; selected game merges schema names/descriptions (`event_handlers.py:121-168,333-378`). | Sequential eager requests; selected game is fetched again. No unlock dates in the UI. Failed/private/no-achievements can be mislabeled alike; a secondary list is capped at 30. |
| Friends | Public friend IDs and summaries, Online/Offline, hover mini-profile, friend selection, double-click to search (`event_handlers.py:40-48,111-119,307-331`; `main_app.py:42-79,329-337`). | Summaries are not batched, no search/sort, network work on mouse movement, failures become empty-looking results. |
| Comparison | Compares searched profile with a selected friend: name/avatar, level, game count, playtime, age, VAC (`main_app.py:118-156`). | Cannot enter arbitrary Profile B. Nullable nested data can crash rendering. Labels assume ownership without authentication. |
| Mutual games | Intersects libraries by AppID and displays each side's integer hours (`main_app.py:159-189`). | Depends on selected friend; no unavailable-library outcome, filtering, or store actions. Sorts AppIDs, not game titles. |
| Inventory | CS2, TF2, Dota 2, PUBG, Rust and Steam; configurable only in source; several community contexts, cursor parameters, description join, amount/type/icon/marketable/tradable fields (`steam_api.py:171-309`). | Partial/error results are not represented, UI caps 400 cards/200 icons per game, row counts ignore stack quantity, pagination compatibility unverified. |
| General UX | Session history of 20 searches, light/dark toggle, clipboard/store actions, context menus, Enter/Ctrl+F, scroll helpers (`ui_helpers.py`; `main_app.py:10-39,91-115,192-345`). | Fixed 900x750 window; global mutable state; no structured module states; no persistent DB or Steam login. |

## Findings and risk register

Line numbers below refer to the unchanged six source files, not to code snippets reproduced in the report. Priorities guide migration; they are not claims of measured exploitation or runtime reproduction.

| Priority | Finding and evidence | Migration response |
| --- | --- | --- |
| P0 | API key is embedded in plaintext HTTP request URLs: `steam_api.py:12,28,41,68,80,92,105,143,158`. Default TLS validation on other HTTPS requests cannot protect these HTTP calls. | HTTPS-only transport; validate destinations; never embed an author key in binaries. |
| P0 | Request exceptions are printed for key-bearing requests, e.g. `steam_api.py:19-20,35-36,49-50,62-63,74-75,86-87,99-100,111-112,152-153`. A requests exception can contain its full request URL and query key. This is a static leakage path, not proof that a specific key was logged. | Return typed safe errors; log operation/status/attempt only; exclude exception objects and query URLs from logs. Tests must search logs for a sentinel key. |
| P0 | Secret-bearing archive exists by filename/metadata; the owner reports a nonempty key. The key value was intentionally not inspected. | Exclude `.env` and old archive from new deliverables; provide placeholder-only configuration. Owner should rotate any key already distributed with this archive. |
| P1 | `search_player` performs summary, bans, level, owned games, recent games, CS stats, prices, friends, every game's achievements, and inventories synchronously in a Tkinter event callback (`event_handlers.py:11-53,91-94,138-149,182`; binding at `main_app.py:325`). `update_idletasks()` does not make this sequence asynchronous. | Cancelable progressive loading; summary first, independent optional modules later. |
| P1 | Environment reads are lowercase (`steam_api.py:7-8`). Uppercase archive keys are owner-reported, not read. On case-sensitive environments these names would differ. | One documented uppercase name; read environment explicitly with no legacy `.env` import. |
| P1 | Eager achievement lookup makes one call per owned game (`event_handlers.py:138-149`); empty list, failure and private response all become false and may be described as no achievements (`:139-159`; `steam_api.py:157-168`). | Lazy selected-game retrieval, explicit `NoAchievements`/`Private`/`Unavailable`/`Failed` states, bounded concurrency and cache. |
| P1 | Hover callback does synchronous summary and image downloads (`main_app.py:42-62`), is bound to `<Motion>` (`:336`), destroys/recreates tooltip each move and reattempts uncached failures. Successful avatars are cached, so it is incorrect to claim every hover always downloads again. | Reuse loaded friend summaries, async deduplicated bounded avatar cache and stable tooltip lifecycle. |
| P1 | All friend IDs are passed to one `get_player_summary` (`event_handlers.py:40-42`); client joins without chunking (`steam_api.py:24-28`). | Chunk to the officially documented summaries limit, map partial batches safely. |
| P1 | Inventory timeout/429 retries repeat the same cursor (`steam_api.py:203-214`). **The loop is bounded:** `page` advances on each attempt and stops after `max_pages=50` (`:189-198`). This mixes retry budget with page budget, then returns partial arrays without a partial outcome (`:235`). `Retry-After` only accepts integer text (`:212`); date-form, malformed, negative, or extreme values are not handled. | Separate finite page and retry budgets, parsed/capped server delay, cancellation, jitter, explicit partial/rate-limited/error outcomes. |
| P1 | Inventory pagination tests only `more` (`steam_api.py:231`) and reads `last_assetid` (`:233`) without missing/repeated cursor protection. Whether this matches the current endpoint was not live-tested by this static audit. | Verify current response contract, test multiple pages, support the actual continuation field, detect repeated/missing cursors. |
| P1 | Inventory UI silently renders `items[:400]` (`event_handlers.py:275`), limits icons to 200 (`:245,299-302`), creates each card eagerly, and uses Tk Label width/height values of 96 while labels initially contain text (`:244,278`). Text-unit sizing is a layout risk. | Virtualized/incremental complete list with bounded lazy images and pixel-aware controls. |
| P1 | Nullable summary is dereferenced (`main_app.py:131,138`); `games=None` and `bans=None` defeat `.get(...,{})` defaults (`:143-150`). Absent bans key defaults to `Clean`; actual API failure storing `None` can instead crash. Mutual-game logic also dereferences nullable games (`:163-164`) and labels an empty result without checking accessibility (`:166-167`). | Propagate availability outcomes; independent Profile A/B inputs; unavailable metrics are N/A and unavailable intersections cannot equal zero. |
| P1 | Price collector starts at zero, skips failed/missing responses, sums `initial` rather than current discounted `final`, and returns a number without completeness metadata (`steam_api.py:116-139`). Currency is overwritten per response, with no mixed-currency guard. UI hides a zero estimate (`event_handlers.py:95`), so the specific claim that UI displays failed requests as zero is **not confirmed**. A positive partial sum is still presented without limitations (`:96`). | Defer prices until endpoint reliability/terms are verified; never describe prices as spending; expose coverage, currency, region, checked time and unknowns. |
| P1 | Asset descriptions are indexed by `(classid,instanceid)` but `_resolve_desc` falls back to instance 0 or any description (`steam_api.py:238-260`), potentially assigning metadata for a different instance. `item_count` is `len(items)` (`:299`), while stack amount is retained only per item (`:291`); summary/UI marketable/tradable totals also count rows (`event_handlers.py:188,256-258`). | Exact pair join, retain context and asset identity, sum amounts separately from asset-row counts; test collisions, unknown descriptions and stacks. |
| P1 | No request generation/cancellation and no executor shutdown path are present. Old icon callbacks retain destroyed labels (`event_handlers.py:229-242`); cache and executor are global (`config.py:14-15`). Network icon bytes load off-thread, but decode/thumbnail happens in the UI callback (`event_handlers.py:218-241`). | Session lifetime cancellation, stale-result suppression, off-thread decode, bounded owned resources. |
| P2 | Global mutable state across all modules (`config.py:1-15`), with unbounded icon cache; friend cache clears every profile change (`ui_helpers.py:73`). Data caches lack TTLs. | ViewModel-owned state, DI, bounded TTL memory cache, explicit refresh timestamp. |
| P2 | Only statuses 0/1 are named; all others display Online (`event_handlers.py:62-64`), and friend statuses collapse all nonzero values (`:118`). VAC and community fields use unchecked indexing (`:71-72`). | Map all documented persona states and nullable fields; no invented trust/fairness score. |
| P2 | Fixed window (`main_app.py:198-199`) and fixed comparison window (`:124-125`); strings embedded throughout. No source-root tests, dependency manifest, README, ignore rules, or CI were found in archive metadata. | Resizable Avalonia layout, resource-based strings, test projects, pinned dependency declarations, CI and honest setup docs. |

## Report/code discrepancies

The report usefully records the original five-tab concept (p.18), in-memory caches/session history (p.21), selected-friend comparison (p.28), six supported inventories and partial icon loading (p.30), and known slow large-profile loading/no table filters (p.47). It is not a test result.

- Report p.45 says all Steam calls use HTTPS. The HTTP URLs listed above contradict this claim.
- Report p.28 says friends are sorted with online users first and mutual games by title. `update_friends_tab` preserves returned order (`event_handlers.py:117-119`); `find_mutual_games` sorts AppIDs (`main_app.py:181`). Neither promised sort is implemented.
- Report p.37 describes resetting `main_user_data` and clearing caches. `clear_all_tabs` resets `selected_friend_data` and friend avatars but does not reset `main_user_data` or clear `icon_cache` (`ui_helpers.py:71-94`).
- Report p.46 advertises a full inventory and speed advantages. Card/icon caps are visible in source, and no reproducible performance measurements were supplied. No speedup claim is adopted.
- Report p.47 mentions assessing account reliability using age/level. The migration intentionally presents factual age/ban fields only; these data do not justify a trust or character judgment.

## What remains unverified

No legacy GUI was launched; no credential was read or reused; no real Steam API request was executed in this static audit; no timing, memory, DPI, OS compatibility, complete inventory pagination, or price reliability measurement was made. SDK/package compatibility and .NET build/test results belong to the implementation validation report. Lack of a static syntax error is not a claim of working behavior.

## Reproduce the safe syntax check

From the sanitized legacy directory, use a trusted Python interpreter with this snippet. It parses source without importing it or reading `.env`:

```python
import ast
from pathlib import Path

for path in sorted(Path('.').glob('*.py')):
    ast.parse(path.read_text(encoding='utf-8-sig'), filename=str(path))
    print(f'AST OK: {path.name}')
```

The bundled interpreter used was `C:\Users\ThinkPad\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe`.
