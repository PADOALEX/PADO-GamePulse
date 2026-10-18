# Windows installer

The 0.8.0 setup EXE installs the verified self-contained Windows x64 application for the current
user. Its default folder is `%LOCALAPPDATA%\Programs\PADO GamePulse`. It adds a Start menu shortcut,
offers an optional desktop shortcut, and registers an uninstaller in Windows Installed apps.
No separate .NET, Steam login, server, account or database is required. The installer neither asks
for nor writes a Steam API key. Local environment configuration remains separate.

The package is unsigned. The owner selected the [MIT license](../LICENSE); no external publication
has been performed. Project and dependency notices remain alongside the application. The installer
preserves unmanaged files and keeps the stable AppId and previous installation folder. Version
protection and hash-verified retirement of obsolete managed files are described in the
[upgrade policy](INSTALLER_UPGRADE.md). Actual cross-version results are recorded in the
[release verification](RELEASE_VERIFICATION.md).

## Build

The source recipe is [GamePulse.iss](../installer/GamePulse.iss). Use the verified
[Inno Setup 7.1.0 x64 compiler](https://jrsoftware.org/isdl.php) and the already verified publish folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Installer.ps1 -PublishDirectory <published-folder> -CompilerPath <verified-compiler-folder>/ISCC.exe -OutputDirectory artifacts/installer
```

The script checks the compiler's SHA256 and Authenticode publisher, the application version,
runtime/notices and excluded credential/test files before compiling. Inno 7 command-line options
are used directly; IDE signing configuration is not imported. An existing target installer is refused.
No compiler is bundled into the source ZIP or installed globally by the build script.

Compiler provenance checked on 2026-10-17:

| File | SHA256 |
| --- | --- |
| Official `innosetup-7.1.0-x64.exe` archive | `0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f` |
| Extracted `ISCC.exe` | `d06ebd38f38e3cee60a3c50cc45bd449d77e0bc6a5cabc607ea9886808e4de1a` |

Both had valid Pyrsys B.V. Authenticode signatures; `ISCC --version` returned 7.1.0. Compiler
version resources are 0.0.0.0, so they are deliberately not used as the version check. Original
[Inno license text](licenses/Inno-Setup-LICENSE.txt) is retained. Review the publisher's current
commercial-use guidance before commercial distribution; this does not select GamePulse's license.

The [official privilege documentation](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)
describes `PrivilegesRequired=lowest`; the recipe uses that non-administrative installation mode.
[Automatic uninstall support](https://jrsoftware.org/ishelp/topic_setup_uninstallable.htm) remains enabled.

## Verify

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Installer.ps1 -InstallerPath <setup.exe> -TestDirectory <fresh-isolated-folder>
```

This intentionally changes the current user's installation registry and Start menu while the test
runs, then uninstalls the exact test application. It refuses existing registrations, shortcuts or
test installs so it cannot replace a user's existing installation. It installs into an explicitly
chosen isolated folder, checks registration/shortcut target, runs native startup and UI Automation,
reinstalls, uninstalls, and verifies that an unmanaged marker survives. Logs and that marker remain
inside the test folder. No key or real profile data is used. For the real 0.7-to-0.8 upgrade check,
use `Test-InstallerUpgrade.ps1` as described in [upgrade verification](INSTALLER_UPGRADE.md).
Use its explicit `-SkipUiAutomation` switch when desktop automation has been stopped; this does
not claim a fresh native accessibility pass. See [executed results](RELEASE_VERIFICATION.md).

Clean-machine Windows acceptance, interactive wizard keyboard/screen-reader review, signing and
external distribution remain release checks. The historical 0.7 setup cannot be retroactively given
the 0.8 downgrade guard; do not use it over a newer installation.
No SmartScreen reputation or clean-machine certification is claimed.
