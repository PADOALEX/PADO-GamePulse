# Contributing to PADO GamePulse

Thanks for helping make Steam profile analysis clearer and more reliable. Small, focused fixes,
accessibility reviews, translations and reproducible bug reports are welcome.

## Before you change code

Read the [architecture](docs/ARCHITECTURE.md), [release verification](docs/RELEASE_VERIFICATION.md)
and [security model](docs/SECURITY.md). Keep the core product boundary: independently entered Steam
profiles, real sources in production, no mandatory login/server/database, and explicit unknown or
partial outcomes. Ask in an issue before introducing a large new dependency or changing that boundary.

For a bug report, include your app/OS version, steps, expected behavior and the visible availability
state. Redact Steam profile details you do not have permission to share. Never attach `.env`, API keys,
cookies, raw credential-bearing URLs, HTTP traces or private profile payloads. Follow
[SECURITY.md](SECURITY.md) for vulnerabilities.

## Local workflow

Use a .NET 10 SDK. From the repository root:

```powershell
dotnet restore PADO.GamePulse.sln --disable-parallel -m:1
dotnet build PADO.GamePulse.sln -c Release --no-restore -m:1
dotnet test PADO.GamePulse.sln -c Release --no-build -m:1
dotnet run --project tests/GamePulse.UiChecks -c Release --no-build -- artifacts/ui-checks
```

The test suites require no Steam credential. Synthetic HTTP/image data belongs only in test projects.
For optional live use, copy `.env.example` to a private `.env` and supply your own permitted key there.
Never fill in the tracked template. `GAMEPULSE_DISABLE_DOTENV=1` disables local-file reads for isolated
diagnostics. Do not deliberately provoke Steam rate limits or bypass privacy restrictions.

## Pull requests

- Explain the user-visible problem, resulting behavior and checks you actually ran.
- Keep Steam DTOs and transport details out of views; preserve cancellation and stale-result guards.
- Preserve nullable facts, coverage, timestamps and the difference between empty and unavailable.
- Put UI text in English/Ukrainian resources and keep keys/placeholders aligned.
- For visual changes, check compact layout, all themes, keyboard focus and both languages. Use clearly
  labeled synthetic screenshots unless you have permission to publish the real data.
- Add focused regression coverage for behavior changes. Never replace a failing assertion with a weaker
  one without explaining the changed contract.
- Exclude build output, local settings and secrets. Use `scripts/Export-SourceArchive.ps1` for a safe
  source archive rather than zipping the entire working folder.

Documentation can record a remaining external check honestly; it must not claim an unrun live,
Narrator, clean-machine or platform test passed. Keep the MIT license and dependency notices intact.
