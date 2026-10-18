# Security and privacy

## Credentials

The original archive contains a 57-byte .env entry. Its contents were not opened, printed or copied.
The nonempty API_KEY statement comes from the owner, not a value inspection. If that archive has been
shared, revoke/rotate its Steam Web API key. Deleting a local copy does not revoke a key.

Set GAMEPULSE_STEAM_API_KEY in the process environment. The application deliberately does not load
the old .env or its lowercase api_key/cs_app_id names. There is no embedded author credential.
Without a key, setup guidance appears. Never send a Steam password, cookie or login token to this app.

The .env.example is documentation only, with an empty value. Do not put real values in commits,
terminal transcripts, screenshots, support tickets, CI or build artifacts.

## Network and logs

Steam API requests use HTTPS with standard TLS validation and no automatic redirect following.
Authentication is a Steam query key where the API requires it. Never log request URIs or raw
HTTP exceptions. Infrastructure emits static outcomes and endpoint labels, not key-bearing URLs.
HttpClientFactory request loggers are removed for the credential-bearing client.
Before any HTTP client starts, Program.Main and the runtime configuration disable
System.Net.Http.EnableActivityPropagation. This prevents the built-in HTTP DiagnosticListener
from publishing credential-bearing request objects. A regression exercises a real SocketsHttpHandler
with an injected no-network connection failure, rather than only checking a fake logger.
Do not enable external HTTP tracing/proxies with credentials; OS-level tracing is outside application control.
Only bounded transient retries are permitted. Retry-After is respected; an excessively long wait is
returned without retry; HTTP 429 remains RateLimited. Client concurrency is an application resource cap,
not a claim about an official universal Steam request rate.

## Data retained

Profile/level/ban/vanity caches and bounded recent search history live in memory only and disappear
when the process exits. The desktop image cache is also temporary. There is no local account database,
telemetry upload or automatic profile archive. Steam sees requests sent to its services.
Do not bypass private profiles or infer hidden facts from absence. Public does not mean unrestricted
redistribution: review Steam terms and applicable obligations before publishing collected data.

## Official references and limits

Reviewed 2026-10-16; ISteamUser friend/summary and ISteamUserStats achievement contracts rechecked 2026-10-17:

- [Web API overview](https://partner.steamgames.com/doc/webapi_overview)
- [ISteamUser](https://partner.steamgames.com/doc/webapi/ISteamUser)
- [IPlayerService](https://partner.steamgames.com/doc/webapi/IPlayerService)
- [ISteamUserStats](https://partner.steamgames.com/doc/webapi/ISteamUserStats)
- [Web API terms](https://steamcommunity.com/dev/apiterms)

Steamworks examples may show the partner host. This independent explorer uses the public
api.steampowered.com host with an owner-supplied permitted Web API key. It does not use a publisher
key or promise every method is available for every account. The documentation also contains
context restrictions for GetPlayerSummaries; distribution/use must comply with Valve's current terms.
No live credentialed integration was performed during this implementation.

## Library/comparison milestone

Owned/recent/level service arguments use documented input_json on the fixed HTTPS API host. Wire
bodies have a 16 MiB limit. Libraries are checked for counts/unique valid AppIDs before they can be
compared. Missing minutes stay nullable. Library cache has explicit row and estimated-byte budgets;
oversized sources are returned intact without caching. Shared transports stop when their final reader
cancels; stale loads cannot repopulate refreshed entries. No new persistent storage or telemetry was added.

## Friends milestone

Friends data is session-only: the separate memory cache defaults to a 3-minute TTL, 30,000 rows and
estimated 16 MiB. Only complete successful directories enter it. Failed/partial enrichment does not
silently reuse stale cached data, and missing summaries retain their SteamIDs with unknown facts.
Request deduplication and cancellation do not bypass Steam access rules; the shared retry/timeout gate
continues to apply. Friend response bodies have an 8 MiB bound and oversized payloads fail explicitly.

The [ISteamUser reference](https://partner.steamgames.com/doc/webapi/ISteamUser) limits summary batches
to 100 IDs and describes private friend lists returning HTTP 401. That status alone is not proof of
privacy: authorization/key/access failures must remain possible explanations. No inference about a
person's honesty, hidden relationships or online state is made from unavailable data.

Mini-profiles use already loaded summaries and request only the selected avatar. The image transport
uses allowlisted HTTPS hosts, bounded bytes/cache/concurrency and cancellation. Hovering a row does not
start an HTTP request. External Steam links are constructed from a validated individual SteamID64,
not trusted from response URLs. Friend navigation cannot inject arbitrary URLs or keys into logging.
The owner still needs to validate permitted live access with a fresh local key; no such check is claimed.

## Achievements milestone

Only an explicitly selected game's achievement sources are requested. The read-only GetSchemaForGame/v2
and GetPlayerAchievements/v1 methods use the owner's permitted user Web API key, not a bundled or
publisher credential. Both pass through the fixed-host HTTPS client, redacted diagnostics, timeout,
bounded retry and shared concurrency policies. No raw source error text is shown or logged. The
[official ISteamUserStats reference](https://partner.steamgames.com/doc/webapi/ISteamUserStats) describes
these method versions and key/language parameters; availability still depends on Steam's response.

HTTP401/403 is Unavailable rather than proof of privacy. Only a recognized explicit player privacy
response is labeled Private. Missing schema fields do not establish that a game has no achievements;
only an explicit empty achievement list does. A schema/player mismatch leaves totals unknown and
retains available details without inventing locked/unlocked flags or hidden descriptions.

Achievement payloads are bounded at 8 MiB and never silently truncated. Independent schema/player-result
memory caches default to 6 hours / 3 minutes and each permit at most 256 entries, 30,000 rows and
estimated 16 MiB. Oversized complete data can be viewed without being cached. Incomplete/error results
are not cached; refresh failures are not hidden by stale success. Request sharing remains cancellable
and refresh invalidates older cache writers. None of these local policies bypasses Steam rate limits.

No achievement snapshot is written to disk. The ViewModel keeps current selected-game details and
compact checked-game status/counts for the session. Aggregates are labeled with checked coverage and
do not imply unqueried library data or daily historical tracking. Test-only synthetic achievement lists
are isolated from production. Credentialed live integration and wider native platform review remain
owner release checks; no such execution is claimed by this implementation.

## Inventory milestone

Inventory uses the read-only `https://steamcommunity.com/inventory/{SteamID64}/{AppID}/{context}`
Community endpoint. Numeric path components are validated before URL construction; no response-provided
host or arbitrary URL is followed. The dedicated HTTP client sends neither Web API keys nor cookies,
uses normal TLS validation and disables redirects. It never requests Steam passwords, automates login,
submits trades or evades CAPTCHA/access controls. HTTP401/403 means inaccessible/Unavailable, not proof
of a private inventory. HTTP429 remains RateLimited; first-page errors never become empty inventories.

The pagination/cursor and description-join behavior was checked against
[Steam's own frontend JavaScript](https://community.fastly.steamstatic.com/public/javascript/economy_v2.js)
on 2026-10-17. This is implementation evidence and may change; it is not a guaranteed published public
Web API contract. The [official IInventoryService reference](https://partner.steamgames.com/doc/webapi/IInventoryService)
describes a publisher service, which this independent public-profile viewer does not use. Version 0.5
did not fetch live inventory JSON. Version 0.6 verified selected public Community inventory responses
without cookies or keys; the dated scope and unavailable cases are recorded in
[live-source verification](LIVE_SOURCE_VERIFICATION.md). No private inventory was accessed.

Retries are bounded to three attempts with backoff/jitter. Retry-After up to 30 seconds is honored;
longer delays stop rather than hammering the source. The Community gate defaults to one concurrent
request (configurable up to two). These local resource policies do not assert an official universal
Steam rate limit. Malformed JSON, changing totals, duplicate assets and repeated cursors fail honestly.
An interruption after valid pages may retain those rows as an explicitly incomplete snapshot with its
reason; global quantity totals remain unknown. No hidden truncation or invented monetary value is used.

An 8 MiB per-page wire bound and configurable total-load safety bounds (default 250,000 assets,
64 MiB received, 1,000 pages) prevent unbounded transport. A reached bound reports incomplete coverage.
The separate session-only cache defaults to 2 minutes, 256 entries, 30,000 rows and estimated 16 MiB;
only complete successful ownership snapshots enter it. Partial/error snapshots are not cached, and
oversized complete inventories stay viewable without caching. Refresh cannot be overwritten by an
older load. Missing/conflicting descriptions and unknown marketable/tradable flags remain explicit.

Icons are created only from validated opaque Steam image paths on a fixed HTTPS CDN. Since 0.9,
visible virtualized rows and the selected item use the existing bounded image service. Hovering does
not fetch images, and scrolling never queues the whole inventory. Owned bitmaps are disposed on replacement; cancellation/generation
guards reject late profile/context/icon results. Inventory is not persisted, logged as a full payload,
exported automatically or used to infer hidden items. Test fixtures remain confined to test projects.


Inventory icons use only HTTPS community.fastly.steamstatic.com/economy/image paths without credentials,
queries or fragments. The default client disables cookies and redirects. Media/body/source dimensions,
concurrency/deadline, decoded size and encoded-byte cache are bounded. Selected-image failures leave
item facts visible. Typed partial-stop reasons prevent exposing raw server messages in the interface.

## Release hardening in 0.6

The Web API transport now also disables cookies and implicit credentials. Avatar transport rejects
query strings, fragments, credentials and non-allowlisted CDN hosts before HTTP. Both source dimensions
(8192 per axis / 16 million pixels) and decoded avatar dimensions (128 per axis) are bounded; only
successfully decoded images enter the TTL cache. A disposed service cannot start or cache new work.
The Windows package includes the pinned .NET security patch and dependency notices; see
[runtime maintenance](WINDOWS_RELEASE.md). Startup smoke failures return a nonzero native process code.

## Local configuration in 0.9

At the owner's request, Desktop now reads its explicit local `.env` at startup. It does not walk
parents, load the Python archive, or modify/persist the process environment. An existing environment
value wins even when empty; launch-directory settings precede executable-directory settings.
Only the API-key and initial UI-language names are allowlisted. The strict UTF-8 reader caps input
at 16 KiB, rejects reparse/nonregular files and malformed supported assignments, and never retains
exceptions or logs values. Literal quoted/unquoted syntax has no variable or command expansion.
The file is ordinary local plaintext controlled by the user; it is not represented as an encrypted vault.

The local key stays out of Git by default ignores and out of build inputs, embedded resources,
portable ZIPs and installers by explicit item exclusions and release-payload checks. Source export
excludes actual local files before reading payload bytes and validates the only allowed `.env.example`
has an empty key. Never manually force-add credentials or distribute the entire local working folder.
Automated startup/release checks use `GAMEPULSE_DISABLE_DOTENV=1`; LiveChecks remains opt-in and
environment-only. The app never displays a stored key and uses no key-bearing image/inventory requests.

Visible row thumbnails use existing allowlisted HTTPS loaders with bounded concurrency/cache/decoding.
Their controls start only inside the effective viewport, cancel/reject stale results on recycling or
hiding, and dispose owned bitmaps. Scrolling does not start a full-library image scan.
