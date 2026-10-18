# Real Steam source verification — 0.6.0

## Scope and privacy

On 2026-10-17, the opt-in [LiveChecks tool](../tools/GamePulse.LiveChecks/README.md) used the **production inventory client and service** against one publicly accessible Steam Community profile. Its SteamID was obtained from that profile's actual inventory page rather than guessed. The page advertised contexts `730/2`, `440/2`, `570/2`, and `753/6`; the selected profile's identity and asset payloads are omitted from distributed evidence. No key, cookie, login, CAPTCHA bypass, or alternate privacy endpoint was used.

The fresh permitted `GAMEPULSE_STEAM_API_KEY` was absent. Consequently this inventory run does **not** verify live profile summaries, libraries, friends, or achievement Web API methods. Missing-key handling and HTTP edge cases are covered by the separate automated tests; they do not establish that every protected Steam method worked live.

## Observed public Community responses

The run lasted from **19:05:35 to 19:06:06 UTC**. Inventory page size was 1000, minimum request-start spacing was three seconds, and retries were disabled. There were **7 inventory requests, 0 retries, and 4 separate CDN image requests**. All four image responses were HTTP 200, `image/png`, with valid PNG signatures. The machine-readable [aggregate report](performance/live-source-results.json) contains the actual results without profile or asset identifiers.

| App / context | HTTP status per page | Availability | Pages | Loaded assets | Units including `amount` | Descriptions joined |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| CS2 `730/2` | 200 | Ready, complete | 1 | 1 | 1 | 1 |
| TF2 `440/2` | 200 | Ready, complete | 1 | 142 | 142 | 142 |
| Dota 2 `570/2` | 200, 200 | Ready, complete | 2 | 1697 | 1697 | 1697 |
| PUBG `578080/2` | 401 | Unavailable | N/A | N/A | N/A | N/A |
| Rust `252490/2` | 401 | Unavailable | N/A | N/A | N/A | N/A |
| Steam Community `753/6` | 200 | Ready, complete | 1 | 117 | **416** | 117 |

The real Community inventory demonstrates that asset rows and item quantities differ. Dota 2 demonstrates successful cursor pagination across two pages and agreement with the source's reported count. Every loaded asset joined a description using `classid` and `instanceid`, and both trade flags were available for every loaded asset. Dota 2 supplied safe image paths for 1695 of 1697 assets; the two absent paths stayed absent. Other successful inventories supplied paths for every row.

HTTP 401 for PUBG and Rust supplies no inventory total. The result remains `Unavailable`, with null counts: it does not establish zero ownership, an empty inventory, a private profile, or positive inventory support for those apps on this profile. No retries, alternate identities, or bypass attempts followed those responses. No HTTP 429 was induced. Empty public inventories, server failures, changing inventories, and rate-limit handling remain deterministic test cases rather than claims of observed live behavior.

## Source contract checked

The primary [Steam Community inventory frontend](https://community.fastly.steamstatic.com/public/javascript/economy_v2.js) constructs `/inventory/{steamid}/{appid}/{context}`, uses `start_assetid`, `more_items`, `last_assetid`, and `total_inventory_count`, and joins description identity by class and instance. Its image helper constructs Steam CDN economy image paths with a size suffix. These are **website implementation details**, not a guaranteed third-party Web API contract. The [publisher inventory Web API](https://partner.steamgames.com/doc/webapi/IInventoryService) has a different authorization and product scope; it is not substituted for arbitrary public profile inventories.

The application's fixed HTTPS Community URL, including its trailing slash before the query, returned direct HTTP 200 in the successful cases with automatic redirects disabled. No redirect fix was needed. The successful one-page CS2 response omitted `more_items`; the existing parser correctly treated that as terminal and required count agreement before reporting completeness. Relative image tokens converted by the production mapper to `community.fastly.steamstatic.com/economy/image/.../256fx256f` returned PNG images. This check verified transport, MIME, and signature; the tool does not perform native bitmap decoding or capture real-profile screenshots.

## Reproduction and remaining work

Run [LiveChecks](../tools/GamePulse.LiveChecks/README.md) with an explicitly chosen allowed public SteamID. It writes aggregate diagnostics only, uses bounded production parsing and cancellation, and stops on HTTP 429. It has no default personal identity and does not run as part of `dotnet test` or normal application startup. No production data was replaced with test responses.

The exact historical counts will change as inventories change. A fresh owner-configured key is still required to complete live Web API acceptance. Positive PUBG/Rust inventories, broader privacy cases, native image decoding against selected live items, and multiple allowed profiles remain explicit verification gaps. The existing parser and pagination transport handled this live matrix correctly; no speculative infrastructure behavior change was made solely to produce a release diff.
