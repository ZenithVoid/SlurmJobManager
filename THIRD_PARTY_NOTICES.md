# Third-Party Notices

This document lists the **direct NuGet dependencies** currently referenced by the projects in `SlurmPilot.sln`.

## Scope and source

- Scope date: 2026-08-20 (UTC)
- Scope: direct `<PackageReference />` items in:
  - `src/SlurmPilot.App/SlurmPilot.App.csproj`
  - `src/SlurmPilot.Infrastructure/SlurmPilot.Infrastructure.csproj`
  - `src/SlurmPilot.Core/SlurmPilot.Core.csproj`
  - `src/SlurmPilot.Updater/SlurmPilot.Updater.csproj`
  - `src/SlurmPilot.VolumeTiffPlugin/SlurmPilot.VolumeTiffPlugin.csproj`
- License metadata source: NuGet V3 catalog (`catalogEntry.licenseExpression` / `licenseUrl`)

> If a dependency license cannot be confirmed from NuGet metadata, verify it from the upstream project before distribution.

## Dependency list

| Dependency | Version | License | Project / Package URL |
|---|---:|---|---|
| AvalonEdit | 6.3.1.120 | MIT | http://www.avalonedit.net/ |
| BitMiracle.LibTiff.NET | 2.4.660 | BSD-3-Clause | https://github.com/BitMiracle/libtiff.net |
| OpenTK | 4.9.4 | MIT | https://github.com/opentk/opentk |
| OpenTK.GLWpfControl | 4.3.6 | MIT | https://github.com/opentk/GLWpfControl |
| Polly | 8.6.6 | BSD-3-Clause | https://github.com/App-vNext/Polly |
| Serilog | 4.3.1 | Apache-2.0 | https://serilog.net/ |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | https://github.com/serilog/serilog-sinks-file |
| SSH.NET | 2024.2.0 | MIT | https://www.nuget.org/packages/SSH.NET/2024.2.0 |
| System.Security.Cryptography.ProtectedData | 10.0.7 | MIT | https://dot.net/ |
| XTerm.NET | 1.0.12 | MIT | https://github.com/tomlm/XTerm.NET |

The OpenTK runtime distribution includes GLFW native binaries under the zlib/libpng license:
https://github.com/glfw/glfw/blob/master/LICENSE.md

## Notes

- This notice list is intentionally lightweight and release-friendly.
- Keep this file updated when adding/removing/upgrading package references.
