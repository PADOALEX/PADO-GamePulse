# Executed validation

Date: 2026-10-16. Host: Windows x64, installed .NET SDK 10.0.102 / .NET runtime 10.0.2.
Scope: Phase A audit and Phase B vertical slice. No live Steam credential was read or used.

## Results

| Check | Actual result |
| --- | --- |
| Legacy static syntax | 6/6 Python files pass ast.parse, 1,269 source lines; no legacy imports or execution |
| Reference availability | 52 PDF pages read as extracted text; original ZIP and documents unchanged |
| NuGet restore | Passed with workspace cache directories and one MSBuild node |
| Release solution build | Passed, 0 warnings, 0 errors after Avalonia 12 API/resource fixes |
| Unit tests | 48 passed, 0 failed, 0 skipped |
| Fake-HTTP integration tests | 32 passed, 0 failed, 0 skipped |
| Native Windows startup | Actual no-key window opens in both themes, captures its own visual tree and exits 0 |
| Screenshot inspection | Dark/light setup screens manually inspected; background, footer cropping and input contrast defects corrected |
| Windows publish | Framework-dependent win-x64 output produced; published GamePulse.Desktop.exe startup exits 0; requires a supported .NET 10 runtime |

The 80 tests exercise input parsing and hostile URLs; every supported persona state; progressive
ViewModel updates; stale summary/enrichment rejection; cancellation; recents bounds; no-key setup;
HTTP 200/403/404/429/500; timeout, invalid/partial JSON; unknown fields; strict wire IDs; Retry-After
date/delta; bounded attempts/concurrency; cache hit/expiry/eviction/force; reversed cache completions;
and sanitized logger/DiagnosticListener outcomes. Test payloads and sentinel keys are confined to tests.
The measured test durations are test-run observations, not live Steam or UI performance benchmarks.

## Commands actually executed

From the solution directory:

```powershell
dotnet --version
dotnet restore PADO.GamePulse.sln --disable-parallel -m:1
dotnet build PADO.GamePulse.sln -c Release --no-restore -m:1
dotnet test PADO.GamePulse.sln -c Release --no-restore -m:1 --logger trx --results-directory <workspace>/work/test-results
dotnet src/GamePulse.Desktop/bin/Release/net10.0/GamePulse.Desktop.dll --smoke-test --smoke-output <solution>/docs/screenshots/setup-windows.png
dotnet src/GamePulse.Desktop/bin/Release/net10.0/GamePulse.Desktop.dll --smoke-test --smoke-light --smoke-output <solution>/docs/screenshots/setup-windows-light.png
dotnet publish src/GamePulse.Desktop/GamePulse.Desktop.csproj -c Release -r win-x64 --self-contained false -m:1 -o ../PADO.GamePulse-win-x64
```

Paths shown as `<workspace>`/`<solution>` above were absolute local paths in executed commands.
The smoke command explicitly removed the API-key variable only in its child process environment.
The published executable was also launched via Start-Process -WindowStyle Hidden -Wait with --smoke-test.
Safe AST and full test-run logs remain in the separate working directory, not the source archive.

## Environment and repair history

- `dotnet --info` showed the installed SDK but returned a Service Control Manager access error.
  `dotnet --version`, restore, compile and tests worked; no workload installation was necessary.
- Initial restore failed writing the default NuGet HTTP cache. Redirecting DOTNET_CLI_HOME,
  NUGET_PACKAGES, NUGET_HTTP_CACHE_PATH and NUGET_PLUGINS_CACHE_PATH to workspace fixed it.
- Parallel solution restore failed without actionable compiler errors in this sandbox. `-m:1` worked.
- First compile found Avalonia 12 clipboard extension/import and obsolete APIs; repaired and rebuilt.
- A delegated test-host launch timed out. The root-context full run executed both suites successfully.
- Independent review found diagnostic URI exposure, permissive wire-ID validation and a cache race.
  All three were repaired and regression-tested before delivery.

## Not verified / not implemented

No credentialed public/private/nonexistent/invalid-key live Steam matrix was run. Real avatar CDN
fetch/decode was not checked with a live profile. There is no live-data screenshot. Linux/macOS,
hosted GitHub Actions, high-DPI matrices, a clean-machine installation and signed installers were
not tested. No first-meaningful-paint, large-library/inventory scrolling or memory benchmark is claimed.
Large library/inventory tests belong to the unimplemented Phase C features, tracked in NEXT_STEPS.md.
The screenshot is the actual empty setup state, with no example account injected into production.

This delivery must not be labeled the completed parity MVP.
