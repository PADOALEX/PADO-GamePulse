# Opt-in live Steam checks

This console tool calls the same production services as Desktop. It is separate from the application, has no fixtures, and is never required for normal use or automated tests. It never loads cookies or legacy configuration files. The public inventory mode does not read an API key; the explicitly selected profile mode reads only `GAMEPULSE_STEAM_API_KEY` from the current process environment.

## Public inventory mode

From the repository root:

```powershell
dotnet run --project tools/GamePulse.LiveChecks -c Release -- --help
dotnet run --project tools/GamePulse.LiveChecks -c Release -- --steam-id YOUR_PUBLIC_STEAMID64 --check-images --out inventory-live-results.json
```

Replace `YOUR_PUBLIC_STEAMID64` with a public profile you are allowed to inspect. No SteamID, profile name, asset identifier, image URL, raw response, or exception text is included in the report. The output contains aggregate counts, availability states, HTTP statuses, timestamps, and optional image MIME/signature checks only. The tool does not decode images or measure native UI performance.

Default apps/contexts are CS2 `730/2`, TF2 `440/2`, Dota 2 `570/2`, PUBG `578080/2`, Rust `252490/2`, and Steam Community `753/6`. Use `--apps 570,753` to restrict the run. `--page-size 1000` deliberately permits pagination with medium inventories; a value from 1 to 2000 is accepted. The real source can return fewer rows than requested.

Requests are sequential with at least three seconds between request starts, including image requests and cursor pages. `--spacing-seconds` accepts 3–60. This is a conservative local diagnostic policy, not a published Steam quota. Each request has one attempt, the run stops on HTTP 429, the overall budget is ten minutes, and each inventory has a 25-page diagnostic safety limit. Interrupted snapshots are explicitly partial. Ctrl+C cancels further work.

Exit code `0` means the requested diagnostic collection completed. It can include an honest `Unavailable` result; inspect every result state. Exit `1` means a malformed response, partial result, failed image check, invalid arguments, timeout/cancellation, or report-write failure. Exit `2` means Steam returned HTTP 429 and no further app/image request was attempted. A blocked or unsupported context is never interpreted as an empty inventory or proof of privacy.

The Community endpoint and image paths are best-effort website interfaces. They can change independently of the supported Steam Web API. The historical release observations are in [LIVE_SOURCE_VERIFICATION.md](../../docs/LIVE_SOURCE_VERIFICATION.md); rerunning today may produce different counts or access states.

## Credentialed profile mode

Set a fresh, authorized `GAMEPULSE_STEAM_API_KEY` locally in the environment of the terminal that runs the tool. Do not put its value in command arguments, source files, reports, screenshots, or chat. The tool does not read `.env` files and does not store the key. If the environment key is absent, the command exits with `MissingKey` and code `3` before constructing the HTTP transport. An optional report records zero requests.

```powershell
dotnet run --project tools/GamePulse.LiveChecks -c Release -- --profile YOUR_PUBLIC_STEAMID64 --out profile-live-results.json
dotnet run --project tools/GamePulse.LiveChecks -c Release -- --profile YOUR_PUBLIC_STEAMID64 --achievement-app 440 --compare-profile SECOND_PUBLIC_STEAMID64 --out profile-comparison-results.json
```

Replace the two identifier placeholders with public profiles you are allowed to inspect. Profile mode accepts canonical SteamID64 inputs, not display names. It checks Summary, level, bans, owned games and recent games through `ProfileLookupService` / `SteamLibraryService`, then the first profile's friend list and summary batches through `SteamFriendsService`. A summary that is not available stops the remaining checks for that side. The optional second side checks its summary, level, bans and libraries, then `ProfileComparisonService` determines mutual games from complete, available libraries. It does not expand the second profile's friend graph.

Achievements are skipped unless you explicitly supply one positive `--achievement-app` AppID. `SteamAchievementService` then requests the selected game's schema and, when usable, the first profile's achievements. No library-wide achievement sweep occurs. The report includes counts and completeness; it omits profile/game names, SteamIDs, AppIDs, API achievement names, URLs, raw responses and exception text. Sides are labeled `A`, `B` and `A+B`. Missing and partial values stay unknown instead of becoming zero.

Profile requests use fixed HTTPS Web API endpoints, normal TLS validation, disabled redirects/cookies, one attempt per request, one transport request at a time, and at least three seconds between request starts. `--spacing-seconds` accepts 3–60 for this mode too. This is a local diagnostic policy, not a claimed Steam quota. The run has an 80-request and ten-minute safety budget. Large friend lists can exhaust that budget; the run then reports `RequestBudgetExceeded` and exits `1`, without silently claiming complete results. The transport prevents every further HTTP request after the first 429, including subsequent friend batches; exit code `2` records the rate limit. Ctrl+C cancels the run. No automatic retry or rate-limit bypass is performed.

Exit `0` means diagnostic collection completed and can include honestly reported `Private`, `Unavailable`, `NotFound`, `Empty` or `NoAchievements` states. It does not assert that every dataset was accessible. Exit `1` means failure, partial data, cancellation/time/request budget, invalid arguments or report-write failure; exit `2` means HTTP 429; exit `3` means no configured key. A single selected game's completion is not a profile-wide achievement metric. This mode has not been verified against live protected endpoints without an authorized key.
