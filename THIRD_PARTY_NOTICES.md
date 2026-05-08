# Third-Party Notices

This document lists the **direct NuGet dependencies** currently referenced by the projects in `SlurmJobManager.sln`.

## Scope and source

- Scope date: 2026-05-08 (UTC)
- Scope: direct `<PackageReference />` items in:
  - `src/SlurmJobManager.App/SlurmJobManager.App.csproj`
  - `src/SlurmJobManager.Infrastructure/SlurmJobManager.Infrastructure.csproj`
  - `src/SlurmJobManager.Core/SlurmJobManager.Core.csproj`
  - `src/SlurmJobManager.Updater/SlurmJobManager.Updater.csproj`
- License metadata source: NuGet V3 catalog (`catalogEntry.licenseExpression` / `licenseUrl`)

> If a dependency license cannot be confirmed from NuGet metadata, verify it from the upstream project before distribution.

## Dependency list

| Dependency | Version | License | Project / Package URL |
|---|---:|---|---|
| AvalonEdit | 6.3.1.120 | MIT | http://www.avalonedit.net/ |
| Polly | 8.6.6 | BSD-3-Clause | https://github.com/App-vNext/Polly |
| Serilog | 4.3.1 | Apache-2.0 | https://serilog.net/ |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | https://github.com/serilog/serilog-sinks-file |
| SSH.NET | 2024.2.0 | MIT | https://www.nuget.org/packages/SSH.NET/2024.2.0 |
| System.Security.Cryptography.ProtectedData | 10.0.7 | MIT | https://dot.net/ |
| XTerm.NET | 1.0.12 | MIT | https://github.com/tomlm/XTerm.NET |

## Notes

- This notice list is intentionally lightweight and release-friendly.
- Keep this file updated when adding/removing/upgrading package references.
