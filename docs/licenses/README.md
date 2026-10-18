# Dependency license provenance

These texts preserve dependency terms. The owner selected MIT for PADO GamePulse;
see the separate [project LICENSE](../../LICENSE). Third-party terms still apply independently.
The source repository commits below are taken from the restored production NuGet package
metadata. License texts were retrieved on 2026-10-17, without modifying their contents.

| Text | Upstream source |
| --- | --- |
| Avalonia-LICENSE.md, Avalonia-NOTICE.md | [Avalonia 12.1.4 commit](https://github.com/AvaloniaUI/Avalonia/tree/2d79ba55fae1b1e71c69f93a668080c9029e6a31), `licence.md` and `NOTICE.md` |
| Avalonia.BuildServices-LICENSE.txt | [Avalonia.BuildServices 11.3.2 commit](https://github.com/AvaloniaUI/Avalonia.BuildServices/tree/777f975b0a0cecf0311273711d56697212c558c0), `LICENSE` |
| MicroCom-LICENSE.txt | [MicroCom.Runtime 0.11.6 commit](https://github.com/kekekeks/MicroCom/tree/76785efcafd91b5902fd19dd11145f6dd655b7b4), `LICENSE` |
| Tmds.DBus-COPYING.txt | [Tmds.DBus.Protocol 0.94.1 commit](https://github.com/tmds/Tmds.DBus/tree/b4a7fed0b878f74cb54f7cca84d2889af4e596ba), `COPYING` |
| Microsoft-LICENSE.txt | [Microsoft.Extensions 10.0.13 source commit](https://github.com/dotnet/dotnet/tree/4099f83977f4d29604a9aeb971fb0a7bc1d5f699), `LICENSE.TXT` |
| ANGLE-LICENSE.txt | `Avalonia.Angle.Windows.Natives` 2.1.27548.20260419 NuGet package, root `LICENSE` (copied verbatim) |

The Windows release script additionally copies all available root license/notice texts
from the actual restored production packages, including SkiaSharp and HarfBuzz native
third-party notices. It copies the exact bundled .NET runtime's `LICENSE` and
`THIRD-PARTY-NOTICES` from its runtime NuGet package after publish. `DEPENDENCIES.json`
records those package versions, declared licenses, source commits and retained texts.
The manifest covers the production project's resolved graph, which can include build
tools and dependencies for other operating systems; test-project packages are excluded.

Inno-Setup-LICENSE.txt retains the original 7.1.0 compiler license from the verified official package. Inno Setup is a build/installer engine, not a NuGet dependency. See [installer provenance](../INSTALLER.md); this does not select a project license.
