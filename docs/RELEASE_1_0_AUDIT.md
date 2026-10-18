# 1.0 release audit

The 2026-10-18 audit reviewed every production Core/Infrastructure file, Desktop composition and
ViewModels/controls/image services, feature views/resources, tests, CI and release/installer scripts.
The findings below are confirmed behaviors, not a claim that arbitrary future defects are impossible.
Executed results and external acceptance limits are in [release verification](RELEASE_VERIFICATION.md).

| Confirmed issue | Resulting change | Relevant files |
| --- | --- | --- |
| A single-profile response could contain duplicate/conflicting or unrelated profile/ban records; the first matching row was selected | Reject ambiguous single-profile responses instead of reporting potentially incorrect identity or ban facts | `SteamApiClient.cs`, `SteamClientTests.cs` |
| .NET wraps cancellation thrown by a LINQ comparer, making a cancelled Friends/Achievements sort look failed | Unwrap cancellation for the caller's cancelled token; retain genuine sorting failures | `FriendQuery.cs`, `AchievementQuery.cs`, `SortingCancellationTests.cs` |
| Updating Friends ItemsSource could transiently clear selection and reload a mini-profile | Guard publication, retain the same matching selected row and clear it only when no longer present | `FriendsViewModel.cs`, `FriendsViewModelTests.cs` |
| Games sort/filter discarded selection even if the same AppID remained | Preserve selection across equivalent rows; clear it when filtered out or switching sources | `GamesViewModel.cs`, `GamesViewModelTests.cs` |
| Pulse localization rebuilt its lists and could clear two-way selection | Notify row presentation in place; preserve ItemsSource and selected object | `InsightsViewModel.cs`, `LiveLanguageChecks.cs` |
| Visible thumbnails and selected details could simultaneously fetch identical images | Bounded fixed request gates serialize matching URLs through the existing byte cache; consumers still own separate bitmaps | `ThumbnailRequestGate.cs`, `AvatarService.cs`, `InventoryIconService.cs`, `AvatarImageChecks.cs` |
| PowerShell 7 could restore absent environment settings as explicitly empty strings | Central null-safe helper preserves absence versus empty values, including script cleanup | `ReleaseSupport.ps1`, release scripts, `Test-ReleaseSupport.ps1` |
| Release input could pass through reparse ancestors or include unwanted credential/test files | Shared ordinary-path and complete-payload guards reject unsafe input before publication | `ReleaseSupport.ps1`, publish/installer/export scripts |
| Git checkout builds append revision metadata to the product version, which strict installer equality rejected | Normalize valid SemVer build metadata for release comparisons; preserve wrong-version rejection | `ReleaseSupport.ps1`, installer scripts and 30-case helper checks |
| Users could inspect filtered analytics but had no explicit report export | Add captured, privacy-aware CSV export with raw facts, scope/coverage, timestamps and spreadsheet safeguards | `DataExport.cs`, `AnalysisExport.cs`, `CsvExportService.cs`, export tests |
| Selected rows/navigation used mixed accents across themes; setup/about wording was imprecise | Consistent theme resources and selection surfaces; visible version; accurate local-key and export guidance | `App.axaml`, `MainWindow.axaml`, `Strings*.resx` |

The audit retained typed availability, independent arbitrary profile comparison, HTTPS/TLS verification,
bounded retries/caches/concurrency, progressive loading, cancellation/stale suppression and full-list
virtualization. It introduced no required account, server, database, production fixture data or legacy key.

## Deliberate limits

Profile lookup in-flight deduplication is an optional optimization; existing caches and independent
cancellation remain. Catalogue prices, co-op classification, historical tracking, personal backlog,
trusted signing and an automatic updater are separate future work. An unsigned 1.0 desktop release
does not certify screen-reader speech, a clean machine, every monitor combination or private Steam
data. Source fixtures remain test-only and screenshots state their synthetic origin.
