# Windows portable release

The portable release target is an unsigned Windows x64 folder containing .NET runtime
10.0.13. Users extract the entire folder and run `GamePulse.Desktop.exe`; they do not need
a separate .NET installation. The owner selected the [MIT license](../LICENSE). The artifacts
remain unsigned; no external publication or automatic update channel was created.

## Reproduce the archive

Use Windows PowerShell 5.1 or PowerShell 7, a supported .NET 10 SDK and network access
for NuGet restore. No global SDK/runtime installation is performed. From the source root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1 -OutputDirectory artifacts/windows
```

The script restores/builds the solution, runs unit/integration tests and the real-view
headless UI assertion suite, then publishes a fresh unique staging directory. It pins
`RuntimeFrameworkVersion=10.0.13` with self-contained `win-x64`, disables trimming/single-file
packing, and checks the resulting runtime configuration. It executes the published native
EXE in dark and light setup states, including the minimum window size, and checks its exit codes
and screenshot outputs. An intentional invalid screenshot parent also must return exit code 1.
The API-key environment variable is removed from this process for these fixture/setup
checks and restored afterward; its value is never displayed or persisted.

The output directory receives `PADO.GamePulse-<Version>-win-x64.zip` and a matching
`.sha256` file. Existing release names are refused rather than overwritten. Staging,
test results and screenshots remain in a unique `build-<Version>-<guid>` directory for
inspection and are outside the portable application's folder. `-SkipVerification`
is available only when equivalent checks have already been completed separately;
the script prints a warning and does not claim that skipped tests passed. `-SkipNativeAccessibility`
separately suppresses interactive UIA checking and writes an explicit skipped result. This was used
for 0.8 after the user stopped native desktop control; the headless accessibility checks still ran.
Eight native smoke captures now include Ukrainian startup at normal/minimum sizes. Set
`GAMEPULSE_UI_LANGUAGE=uk` before launch; the `uk` satellite resources are required in the archive.

Publishing command used by the script:

```powershell
dotnet publish src/GamePulse.Desktop/GamePulse.Desktop.csproj -c Release -r win-x64 --self-contained true -m:1 -p:RuntimeFrameworkVersion=10.0.13 -p:TargetLatestRuntimePatch=true -p:PublishTrimmed=false -p:PublishSingleFile=false -o <fresh-folder>
```

If publishing manually, run `scripts/Export-DependencyNotices.ps1` against the restored
Desktop `obj/project.assets.json` and fresh publish folder before archiving. It requires
the actual pinned runtime package's license and third-party notices and fails if a
production dependency has no collected license text. The generated dependency manifest
does not copy local package paths, environment settings, tests or fixture payloads.

## Runtime and dependency maintenance

Microsoft's [official .NET 10 download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
and [release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
identified 10.0.13 as the current security patch on 2026-10-17 (released 2026-10-13).
The .NET 10 line is LTS, with support ending 2028-11-14 according to that metadata.
[Avalonia 12.1.4](https://www.nuget.org/packages/Avalonia/12.1.4) was the latest stable
Avalonia package checked on the same date. These are dated checks, not guarantees
that a future download remains current.

Microsoft's [deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/)
explains that a self-contained runtime does not roll forward to machine-wide .NET
security patches. Recheck official metadata, update the script's pinned version,
rebuild and reissue the archive when its bundled runtime needs an update. Retain
`THIRD_PARTY_NOTICES.md`, `DEPENDENCIES.json` and the entire `third-party-notices/`
folder alongside the executable. [License provenance](licenses/README.md) records
the original texts without selecting the owner's project license.

## Validation boundaries

Native setup smoke checks validate launching this artifact on the build machine.
They do not prove clean-machine, every Windows version, high-DPI, live Steam access,
all inventory contexts or hosted CI execution. Credentialed Steam checks require
the owner's fresh locally configured key; they must not read the legacy archive's key.
The application uses real Steam transports in normal operation and never turns UI
test fixtures into live profile results.

Before external distribution, retain the MIT project license and dependency notices and choose
an Authenticode signing identity if trusted signed delivery is required. External publication,
auto-update and multi-platform native certification remain separate release tasks.

## Installer and accessibility checks in 0.7

The pipeline now captures dark/light/high-contrast native setup and runs [native UIA checks](../scripts/Check-WindowsAccessibility.ps1). Build the matching [per-user installer](INSTALLER.md) from its verified publish folder. Actual results are in [DPI/installer verification](DPI_ACCESSIBILITY_VERIFICATION.md).

## Local settings and source export in 0.9

The distributed portable/installer payload never contains `.env` or `.env.*`. Create your private
`.env` beside the EXE after extraction/installation; it is not an installer-managed file. The Desktop
loader supports that file as fallback to explicit environment values. Release/startup tests disable
local configuration reads so a developer's private key cannot alter test state or enter reports.
Use `scripts/Export-SourceArchive.ps1` for source delivery; it excludes local settings before reading
file bytes, validates the empty template and refuses existing archives. Do not zip the entire workspace.
