# Architecture decisions

Status: 0.8.0 local release. All core modules implemented; dated acceptance boundaries are in [release verification](RELEASE_VERIFICATION.md). Earlier milestone sections describe the architecture as it evolved.

## Product boundary

GamePulse explores independently entered Steam accounts. No Steam sign-in, account registration,
database, mandatory backend or production fixtures. Steam is the source of available facts.
The Python archive remains unchanged; only six source files were extracted to a separate audit workspace.
The legacy environment, IDE files, bytecode and credentials are not part of this solution.

## Layers

| Project | Responsibility |
| --- | --- |
| GamePulse.Core | Typed outcomes, nullable source facts, identifier parsing, service contracts, search generation guard |
| GamePulse.Infrastructure | HTTPS Steam DTOs/mapping, bounded retries, cancellation, DI, in-memory TTL cache |
| GamePulse.Desktop | Avalonia window/styles, resource strings, MVVM state, progressive summary, session recents |
| GamePulse.UnitTests | Input/domain and stale-result rules, no network |
| GamePulse.IntegrationTests | Actual infrastructure with injected fake HTTP, no Steam credentials |
| GamePulse.UiChecks | Test-only headless view/virtualization/scroll assertions with labeled synthetic fixtures |

Core depends on the BCL only. Infrastructure and Desktop depend on Core. Desktop is the composition
root; it uses Microsoft.Extensions.DependencyInjection and HttpClientFactory.
There is no Application project until orchestration complexity justifies one.

## Search lifecycle

1. Normalize the SteamID64 / supported Steam Community URL / exact vanity candidate.
2. Reject malformed input and unsafe URL hosts. A display name is not a global search key.
3. Cancel the previous generation and clear its visible state.
4. Resolve vanity only when needed, then request the summary.
5. Display identity immediately; load level, ban facts and avatar independently.
6. Apply any completion only when its generation remains current, including when a transport ignores cancellation.
7. Cancel work and dispose owned resources when the window closes.

Absent persona/visibility/timestamps/ban fields remain unknown. A missing ban response never
means a clean account. An unavailable level does not remove a valid summary.

## Version decisions

Target: net10.0 (LTS), with global.json accepting stable .NET 10 feature bands.
Installed SDK during implementation: 10.0.102, runtime 10.0.2. These are older than the current
Microsoft patch level; update the machine SDK/runtime before distributing a release.
Avalonia 12.1.4 and Microsoft.Extensions.Http 10.0.13 were verified against official package feeds
on 2026-10-16. Packages use explicit versions; builds verify actual compatibility.

Sources: [Microsoft .NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0),
[Avalonia package](https://www.nuget.org/packages/Avalonia/12.1.4),
[Microsoft.Extensions.Http](https://www.nuget.org/packages/Microsoft.Extensions.Http/10.0.13).

## Scope decisions

Price estimation is deferred: catalogue prices are not historical spend and the legacy store endpoint
is not treated as a guaranteed public contract. Inventory uses explicit partial outcomes and validated
pagination over the read-only Community endpoint. Summary, Games/recent and Friends load asynchronously;
Achievements requests start only for an explicitly selected game. No automatic library-wide achievement
scan or inventory request is started. Future screens must preserve typed availability and progressive loading.

The owner selected MIT for GamePulse; dependency licenses remain separate. No official Valve affiliation is implied.

## Phase C additions

ILibraryService is separate from profile lookup. Owned/recent transports share the existing safe client;
IPlayerService calls use input_json. Wire bodies are bounded at 16 MiB. DTO mapping validates counts,
unique AppIDs and nullable minutes/timestamps. "Complete" is relative to Steam's returned count.
Only explicit zero counts produce Empty; absent/inconsistent lists are unavailable, never truncated success.

SteamLibraryService has independent 3 / 2-minute TTLs, shared 100k-row / estimated 32 MiB budgets,
same-key inflight deduplication and observer-aware cancellation. Refresh displaces old cache writers.
Oversized entries remain available intact without caching. Network parsing/mapping runs off the UI context.

GamesViewModel owns load/filter generations and debounced off-thread LibraryQuery work. Each
ComparisonSideViewModel owns its own identity/enrichment/cancellation. ProfileComparisonService
intersects only complete available libraries; ComparisonViewModel computes/filters off-thread.
Finite Grid rows host virtualized lists; fact cards scroll independently in compact windows. Root navigation
uses ancestor-scoped visibility bindings so feature DataContexts cannot accidentally overlay pages.

## Friends milestone additions

Core defines SteamFriend, FriendDirectory, IFriendsService, FriendAction and pure FriendQuery rules.
A relationship's presence in the list is independent of its optional summary; a missing summary is
neither offline nor proof of a private/nonexistent account. Search retains ID-only rows. Unknown dates
and presence sort after known data; the online filter accepts only documented known online states.

ISteamFriendsClient separates wire contracts from SteamFriendsService orchestration. GetFriendList
retains all validated unique individual SteamIDs. GetPlayerSummaries validates requested membership
and duplicates; enrichment is split into batches of at most 100 IDs, following the
[ISteamUser contract](https://partner.steamgames.com/doc/webapi/ISteamUser). Wire bodies are bounded
at 8 MiB; oversized/malformed responses fail explicitly. Two concurrent summary batches is a local
scheduling cap, subject to the shared HTTP gate, not a universal Steam request-rate claim.

SteamFriendsService keeps a separate 3-minute cache bounded by 30,000 rows, estimated 16 MiB and the
configured entry count. Only complete successful enrichment is cached; partial results retain all IDs
without caching a transient failure. Oversized directories remain viewable without caching. Pending
identical reads share one transport; the last departing observer cancels it. Refresh supersedes older
cache writers, and a refresh failure is not hidden by stale data.

FriendsViewModel owns load, filter and selected-avatar generations. Debounced filtering/sorting runs
off-thread over the whole directory. The selected row is preserved at filter commit only if still
visible; an old filter must not reset a later selection. FriendsView hosts a virtualized ListBox in a
finite grid row, plus an independently scrollable mini-profile. Text tooltips use loaded data. Only
the selected avatar is requested; IAvatarLoader enables test-only image fakes without production fixtures.

FriendAction conveys a validated numeric ID and Explorer/CompareA/CompareB destination. The root
ViewModel opens Explorer or loads only the addressed comparison side. The other side retains its
identity, facts, library and cancellation scope. Each affected scope still rejects obsolete results.
Actions are detached and owned bitmaps/cancellation scopes are disposed with the window.

## Achievements milestone additions

Core separates AchievementDefinition / AchievementSchema from PlayerAchievement / PlayerAchievements.
GameAchievements joins by exact API name and exposes nullable counts and percentage. Only a complete
join with known player flags establishes numeric progress; a mismatch retains the union of available
rows with unknown flags where needed. The pure AchievementQuery searches display name, description
and API name; known locked/unlocked filters exclude unknown flags. Stable name/status/date sorts do
not manufacture an unlock date. No hidden row limit is introduced.

AchievementProgress.Calculate and CalculateCounts return distinct weighted completion (sum unlocked /
sum available) and arithmetic mean of per-game completion. Only complete positive-total games enter
either denominator. Zero-achievement, unknown or partial games do not become 0% games. Compact
AchievementCountSnapshot inputs let the desktop retain checked coverage without retaining every
visited game's full payload. These aggregates describe the supplied checked subset, never an unscanned profile.

The existing secret-safe SteamApiClient calls GetSchemaForGame/v2 and GetPlayerAchievements/v1, with
English metadata and fixed HTTPS endpoints. Schema-first orchestration avoids a player request when
an explicit empty schema establishes NoAchievements. Omitted achievement fields remain unavailable;
generic authorization failures do not prove privacy. A recognized explicit privacy error can report Private.
Unknown server text is not reflected into logs or user-facing errors. The wire safety bound is 8 MiB;
oversized responses fail rather than silently truncating rows. Official contracts were checked against
[ISteamUserStats](https://partner.steamgames.com/doc/webapi/ISteamUserStats).

SteamAchievementService has separate schema-by-AppID and player-result-by-SteamID/AppID memory
caches. Defaults are 6-hour / 3-minute TTLs; each has 256-entry, 30,000-row and estimated 16 MiB budgets.
Oversized results remain viewable without caching. Incomplete/error outcomes are not cached. Shared
in-flight reads use observer-aware cancellation; the final observer stops transport. Force refresh
displaces earlier cache writers. The shared HTTP concurrency and bounded Retry-After policy remain in
force; local cache/concurrency values are not universal Steam rate-limit claims.

AchievementsViewModel starts from the available library without requesting every game's achievements.
Selection owns cancellable load/filter generations; changes of profile/game, refresh and disposal
prevent obsolete results from replacing current details. It retains only the selected full payload and
compact checked-game state/counts. Filtering runs off the UI thread. Finite layout rows host virtualized
game/detail lists, while explanatory availability/progress copy makes an unvisited game different from
NoAchievements or unavailable player data. English UI text remains in resources.

## Inventory milestone additions

Core separates InventoryRequest (validated individual SteamID64, positive AppID, canonical numeric
context) from InventoryItem asset rows and InventorySnapshot coverage. Context defaults live in
InventoryCatalog and are editable in the UI. The six requested apps remain independently selectable;
no context is assumed valid for every profile. InventoryQuery searches all name/type/asset/class/instance
fields and filters only known-true marketable/tradable flags. Missing metadata stays addressable by
asset ID. Name/type sorts place unknown text last; numeric asset IDs break ties deterministically.

InventoryStatistics separates asset count from sum(amount). Decimal sums retain exact ulong quantities
without UInt64 overflow. LoadedUnits always describes loaded rows; TotalUnits is nullable when coverage
is incomplete. MarketableUnits/TradableUnits describe only known-true loaded subsets, with known-flag
coverage available separately. Missing flags never imply false and partial sources never establish totals.

The dedicated Community inventory client sends read-only GETs to a fixed HTTPS host/path with numeric
segments. It sends no API key, cookie, login or publisher credential and does not follow redirects.
Assets/descriptions join on the exact classid + instanceid pair; multiple assets sharing a description
retain separate identity and amount. Missing/conflicting descriptions remain unknown rather than being
fabricated. Validated cursors, repeated-cursor protection, count consistency and duplicate detection
guard paging. Mid-load interruption retains prior valid assets with IsComplete=false and the explicit
InterruptionState. A first-page failure has no snapshot; only validated explicit zero establishes Empty.

Steam's [inventory frontend source](https://community.fastly.steamstatic.com/public/javascript/economy_v2.js)
is primary evidence for the Community path, more_items/last_assetid/start_assetid and description joins.
It is not a published stable public Web API contract. The official
[IInventoryService](https://partner.steamgames.com/doc/webapi/IInventoryService) serves publisher use and
is not used for arbitrary Community inventories. No credentialed or live inventory JSON check is claimed.

Default local policies are 2-minute TTL, 256 entries, 30,000 rows and estimated 16 MiB for the separate
inventory cache. Only complete Ready/Empty snapshots enter it; oversized complete results remain
viewable without caching. Missing descriptions may be cached only as explicitly unknown metadata.
Pending identical reads share transport with observer cancellation; refresh supersedes stale writers.
The local Community gate defaults to one request at a time (configurable to two), not a Steam rate claim.
Pages request up to 2,000 assets, with an 8 MiB per-page wire bound. Default total-load safety bounds
are 250,000 assets, 64 MiB received and 1,000 pages; reaching a bound reports incomplete coverage.

InventoryViewModel loads only on an explicit action for the selected app/context. Profile/app/context
changes, cancellation and disposal invalidate load/filter/icon generations. Debounced queries run off
the UI thread and virtualized rows use finite layout space. The selected item's icon alone uses the
bounded allowlisted image loader; no per-hover requests or unbounded per-row bitmaps are retained.
The detail panel and availability/coverage text distinguish unrequested, loading, complete, partial,
empty, inaccessible, rate-limited and failed states. UI strings remain in English resources.

## Accessibility and localization in 0.8

LiveStatusTextBlock preserves the normal TextBlock peer/name events and changes only live-setting
eligibility: effectively hidden or detached controls return Off. Native notification emission remains
the Avalonia provider's responsibility. Current-page status uses the real Button ItemStatus and
headings use actual heading levels. Tests exercise these real peers without simulating audible speech.
Six neutral English resource modules have complete Ukrainian satellites. UiLanguage selects en/uk
before XAML construction using GAMEPULSE_UI_LANGUAGE and does not change CurrentCulture formatting.
Unit tests pin the default UI culture independently of the host OS, with isolated selection tests.
The installer keeps the stable identity, compares numeric versions and embeds only reviewed historical
file hashes for obsolete-payload retirement. See [upgrade policy](INSTALLER_UPGRADE.md).
