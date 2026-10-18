# Windows upgrade and rollback policy — 0.8.0

The per-user installer keeps the `0.7.0` product identity and installation directory. A normal update
replaces the managed application payload, preserves the desktop-shortcut task selected previously,
and retains unknown files in the application directory. No Steam key or persistent profile history is
created or migrated. The installer never requests Steam credentials.

The setup's technical minimum is Windows 10 build 14393 (1607), matching the oldest currently listed
Windows client in [.NET 10's supported-OS matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md).
This does not promise support for every Windows 10 edition or certify GamePulse on that oldest OS:
Microsoft's current servicing/lifecycle restrictions still apply, and the executed native checks use
the present Windows 11 machine. A supported Windows 11 x64 installation is the recommended target.

## Version protection

`GamePulse.iss` reads only this product's current-user Installed apps registration. It uses Inno's
documented [StrToVersion](https://jrsoftware.org/ishelp/topic_isxfunc_strtoversion.htm) and
[ComparePackedVersion](https://jrsoftware.org/ishelp/topic_isxfunc_comparepackedversion.htm), so version
comparison is numeric (`0.10.0` is newer than `0.9.0`). A newer installed version, a missing version or
an unrecognized version stops installation before payload writes. The check runs at initialization
and again in `PrepareToInstall`. An installer mutex prevents concurrent new setup instances in the
same Windows session. Equal versions may be reinstalled; lower versions may be upgraded.

This protection starts with the `0.8.0` installer. The already released `0.7.0` installer cannot gain
a guard retroactively. Running that older executable can still replace a newer installation. Keep
older installers only for deliberate rollback testing. To intentionally roll back, uninstall the
newer version first and then install the desired older release; the app's local environment-variable
configuration remains separate. No automatic updater, rollback service or downgrade bypass switch
was added.

The stable [AppId](https://jrsoftware.org/ishelp/topic_setup_appid.htm) lets successive installers
append to the same uninstall log. [UsePreviousAppDir](https://jrsoftware.org/ishelp/topic_setup_usepreviousappdir.htm)
restores the installed location. The normal Windows uninstaller removes files recorded across the
installation history and both selected shortcuts. As with standard Inno uninstall behavior, a file
originally installed by GamePulse remains managed even if someone edits it later. Unknown user files
are preserved; do not store personal data in managed DLL/PDB/notices files.

## Retired managed files

A recursive directory wipe would remove unknown files and is never used. Instead, the build reads
source-controlled release inventories in `installer/payload-history/`. The `0.7.0` inventory was
generated from its actual portable ZIP after CRC and SHA256 verification:

`98f0d54856042950fced641a8112f23b4a24fcf9c8913ac8bb3ae48a8117490c`

It records 286 relative paths and their SHA256 hashes, with no profiles, keys or machine paths.
`New-InstallerRetirementInclude.ps1` rejects traversing, absolute, reserved, duplicate or injectable
paths and malformed hashes. A path still present in the new publish is always excluded from deletion,
using a case-insensitive comparison. The generator compiles only literal obsolete path/hash pairs
into setup. Setup never reads an installed manifest or accepts an arbitrary user-supplied cleanup list.

After the new payload is installed, a retired file is removed individually only when its content
matches a known released hash and its path components are accessible ordinary files/directories.
[GetFileAttributesW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileattributesw)
rejects paths through reparse points. Modified or unverified files remain with a diagnostic in the
installer log. Known obsolete files participate in Inno's
[Restart Manager resource checks](https://jrsoftware.org/ishelp/topic_isxfunc_registerextracloseapplicationsresource.htm).
If a verified file cannot be removed, setup reports the incomplete cleanup and asks for a rerun
after closing GamePulse. It does not force a process closed or delete an entire directory.

For a future release, independently verify the previous released ZIP before adding its inventory.
Review the generated `retirement-build-<version>.json` and `retired-managed-<version>.iss` beside the
installer build. A compile-time allowlist is a maintenance requirement, not a substitute for checking
the newly produced dependency/runtime graph. Retired directories may remain empty; their recursive
deletion is intentionally unnecessary.

## Reproduce the checks

Use a fresh test directory and an existing verified portable publish. Do not run install tests in
parallel: they intentionally create this product's HKCU registration and shortcuts temporarily.
The script refuses any pre-existing product registration, Start menu shortcut, desktop shortcut or
test directory, and cleanup only invokes the uninstaller registered at the exact test location.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-InstallerRetirement.ps1 -TestDirectory <fresh-generator-test-folder>
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-InstallerUpgrade.ps1 -PreviousInstallerPath <0.7.0-setup.exe> -InstallerPath <0.8.0-setup.exe> -PublishDirectory <verified-0.8.0-publish> -TestDirectory <fresh-upgrade-test-folder> -CompilerPath <verified-ISCC.exe>
```

Use `-SkipUiAutomation` when native UI Automation is unavailable or desktop-control review has been
stopped. This still performs the unattended installation, hashing, native app's self-contained smoke
mode and uninstall checks, but explicitly records `UpgradedNativeAccessibility: false`. Headless peer
tests and earlier native UIA evidence must not be reported as a fresh installed-app UIA run.

The generator check exercises path/hash validation without installation. The native upgrade script
installs the actual `0.7.0` package, selects its optional desktop shortcut, launches it without a key,
upgrades without a `/DIR` or `/TASKS` override, compares every installed payload hash, runs startup
and native UI Automation, and reinstalls the same new version. It temporarily writes a clearly
synthetic future version and malformed version into **its own** product key to exercise the new
installer's nonzero rejection path, then restores the actual version. It does not claim the old
`0.7.0` installer rejects a downgrade.

With `-CompilerPath`, a separate `TEST-ONLY-retirement-fixture` copies the new publish and omits two
historically managed PDBs. An unchanged old PDB must be removed; a deliberately edited old PDB must
survive the update. This fixture is never the release payload or distributed installer. The final
uninstall must remove managed application files, registration and both shortcuts while retaining
the unknown marker. Logs and JSON evidence remain inside the isolated workspace test folder.

## Executed results

On 2026-10-17, the verified Inno 7.1.0 compiler built the actual `0.8.0` installer successfully.
The [native upgrade evidence](performance/release-080-installer-upgrade-results.json) records:

| Check | Actual result |
| --- | --- |
| Released `0.7.0` to actual `0.8.0` | Passed; previous directory and selected desktop task preserved |
| Installed payload | All 288 source payload file hashes matched |
| Native app startup | Previous and upgraded setup-screen captures produced; smoke exit 0 |
| Same-version reinstall | Passed; unknown marker preserved |
| Future `0.10.0` registered version | Current installer rejected with exit 1 and exact newer-version diagnostic |
| Malformed or missing registered version | Both rejected with exit 1 and their expected diagnostic |
| Actual retired production files | Zero; the new payload adds Ukrainian resources and project license |
| Separate test-only retirement package | Known unchanged PDB removed; edited retired PDB preserved during update |
| Actual and fixture uninstall | Registration, managed files and both shortcuts removed; only unknown marker remained |
| Generator safety checks on Windows PowerShell | [16 passed](performance/release-080-retirement-generator-results.json) |
| Upgraded-app UI Automation | Skipped explicitly; `UpgradedNativeAccessibility: false` |

No registration or shortcut from the isolated tests remains. The test-only setup was not copied to
release outputs. The released setup's SHA256 is
`16b5fc664a820f2ce74af5cefe791b4a781ee9dda8c5155ef461fb689ad3efc8`.

See the [release report](RELEASE_VERIFICATION.md) for the rest of the application checks.
The scripts do not establish clean-machine acceptance,
interactive wizard spoken output, signing trust/SmartScreen reputation or full crash/power-loss
recovery. The installer remains unsigned. The owner selected the project's [MIT license](../LICENSE);
third-party components retain their accompanying notices and licenses. Signing identity and any
external distribution remain separate owner decisions.

## 0.9.0 additional verification — 2026-10-18

The immutable released `0.8.0` Windows ZIP was independently checked against its previously
delivered SHA256, the shared checksum list and its sidecar:

`17378ad091f484e829cf01f99d1dcc3ee915d53a4e10b2d0c5686e4121e2586f`

All ZIP CRC checks passed. Its 288 application-relative paths and per-file SHA256 values are recorded
in `installer/payload-history/0.8.0.json`. The archive's enclosing portable directory is omitted from
those installed-path entries. Neither this manifest nor setup contains a local `.env` file.

The upgrade test now creates a deliberately synthetic, non-secret `.env` fixture inside its isolated
installation directory. It verifies that upgrade, same-version reinstall, every version-rejection
path, retired-file cleanup and uninstall preserve that unknown file byte for byte. No pre-existing
user `.env` is read. After uninstall, exactly two unmanaged test files must remain: the original marker
and this `.env` fixture. Dotenv loading is disabled for child smoke/accessibility processes, so even
the synthetic placeholder cannot produce a real Steam request.

The `0.8.0` executed results above remain historical evidence. On 2026-10-18, the verified Inno 7.1.0
compiler built the actual `0.9.0` installer and the complete native `0.8.0` → `0.9.0` run passed.
Fresh [upgrade evidence](performance/release-090-installer-upgrade-results.json) and
[installed-app UI Automation evidence](performance/release-090-installed-accessibility-results.json)
record the following:

| Check | Actual 0.9.0 result |
| --- | --- |
| Released `0.8.0` to actual `0.9.0` | Passed; prior directory and selected desktop shortcut task preserved |
| Installed payload | All 288 source payload file hashes matched |
| Previous and upgraded native startup | Both screenshots produced; smoke exit 0 |
| Installed-app UI Automation | Passed; seven navigation commands, editable search, invalid-input status, live language labels/status and preserved input |
| DPI awareness | Installed app reported PerMonitorV2 at 120 DPI (125%); this run does not constitute a new multi-monitor hardware test |
| Same-version reinstall | Passed; payload and both unmanaged fixtures preserved |
| Synthetic future `0.10.0`, malformed and missing registered versions | All rejected with exit 1 and the exact expected diagnostics; payload and unmanaged fixtures unchanged |
| Local `.env` preservation | Dummy fixture unchanged through upgrade, reinstall, all rejection paths, retirement cleanup and both uninstalls |
| Actual retired production files | Zero; both `0.7.0` and `0.8.0` historical inventories were used |
| Separate test-only retirement package | Unchanged historical PDB removed; deliberately edited retired PDB preserved during upgrade |
| Actual and fixture uninstall | Managed files, registration and both shortcuts removed; exactly the marker and dummy `.env` remained |
| Retirement generator validation | [16 checks passed](performance/release-090-retirement-generator-results.json) |

No product registration or shortcut from the isolated tests remains. The fixture installer stays in
the work directory and is not the distributed setup. The actual `0.9.0` setup SHA256 is:

`4ed09b7a33b6bb19b375ffc1c67ecd49d002dd655f8eacdf442bfc81fee00420`

The evidence's `LegacyInstallerDowngradeProtected: false` retains the existing historical limitation
for the already released `0.7.0` setup; it does not say the current installer lacks version protection.
The current run exercises the `0.9.0` installer's rejection paths. Native UIA verifies accessible
properties and actions; `NarratorSpokenReview` remains false. The same-machine run also leaves
`CleanMachine` and `Signed` false. No claim of a signed release, clean-Windows acceptance or completed
spoken Narrator review follows from these results.

## 1.0.0 — actual update verification (2026-10-18)

The final unsigned 1.0.0 installer was tested over the released 0.9.0 installer. Exact payload hashes,
existing installation directory and desktop task, startup/native UIA, same-version reinstall,
newer/ambiguous-version rejection and uninstall passed. Synthetic local .env and a user marker survived
upgrade, reinstall, every rejection, conservative retirement cleanup and uninstall. The separate
retirement fixture removed an unchanged old PDB and preserved a locally modified one.

[Actual 1.0 upgrade evidence](performance/release-100-installer-upgrade-results.json) retains all booleans.
No real user configuration was read or changed. Native UIA does not certify Narrator speech. Signing,
a clean machine and physical monitor checks remain separate acceptance.
