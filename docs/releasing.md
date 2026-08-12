# Releasing ERP Doctor

ERP Doctor uses `.github/workflows/release.yml` for repeatable packaging and releases.

## Outputs

A successful packaging run creates:

```text
erp-doctor-win-x64.zip
erp-doctor-linux-x64.tar.gz
erp-doctor-plugin-postgres.zip
erp-doctor-plugin-docker.zip
erp-doctor-plugin-nginx.zip
erp-doctor-plugin-redis.zip
ErpDoctor.Tool.<version>.nupkg
ErpDoctor.PluginSdk.<version>.nupkg
checksums.txt
```

The Windows and Linux archives are self-contained .NET 8 builds. PostgreSQL, Docker, Linux/Nginx, and Redis are shipped as separate provider bundles because plugins remain an explicit trust/install boundary.

## Dry run

The Release workflow supports `workflow_dispatch` for manual packaging validation. Maintainer automation can also use a branch named `agent/release-dry-run-*`; branch dry runs package/upload artifacts but are explicitly prevented from creating a GitHub Release or publishing NuGet packages.

Use a development SemVer such as:

```text
0.13.0-dev.1
```

A dry run:

- restores, builds, and tests the solution,
- packs the global tool and Plugin SDK,
- publishes self-contained `win-x64` and `linux-x64` builds,
- publishes PostgreSQL, Docker, Linux/Nginx, and Redis provider bundles,
- runs the standalone Linux binary,
- verifies that the standalone binary can discover all bundled provider DLLs with expected check counts,
- creates ZIP/tar archives,
- generates and verifies SHA256 checksums,
- uploads the files as a workflow artifact,
- does **not** create a GitHub Release,
- does **not** publish to NuGet.org.

This is the required validation path before creating a release tag.

## Real release

A tag matching `v*.*.*` triggers the same pipeline and then creates a GitHub Release.

Example:

```bash
git tag v0.13.0
git push origin v0.13.0
```

The release tag is the source of truth for published package versions. The workflow passes `-p:Version=<tag-version>` to build/pack/publish.

## Global tool package

The CLI package ID is:

```text
ErpDoctor.Tool
```

Normal CI packs a synthetic `0.0.0-ci` version, installs it into a clean temporary tool path, and executes:

```bash
erp-doctor --help
```

When published to NuGet.org, users can install/update with:

```bash
dotnet tool install --global ErpDoctor.Tool
dotnet tool update --global ErpDoctor.Tool
```

## Provider bundles

Provider plugins are released separately from the standalone ERP Doctor binary:

```text
erp-doctor-plugin-postgres.zip
erp-doctor-plugin-docker.zip
erp-doctor-plugin-nginx.zip
erp-doctor-plugin-redis.zip
```

Each archive contains its provider DLL/runtime dependencies when applicable, provider documentation, example configuration, and MIT license.

Release smoke testing verifies the self-contained ERP Doctor binary can load:

- PostgreSQL: 4 checks,
- Docker: 3 checks,
- Linux/Nginx: 3 checks,
- Redis: 5 checks.

The smoke test validates provider discovery/loading only. It does not execute PostgreSQL, Docker, Nginx, or Redis diagnostics against live services. Live-service permissions, authentication, and availability remain deployment-specific runtime concerns.

## NuGet.org publishing

If the repository has an Actions secret named `NUGET_API_KEY`, a real tag release additionally pushes:

```text
ErpDoctor.Tool.<version>.nupkg
ErpDoctor.PluginSdk.<version>.nupkg
```

to NuGet.org with `--skip-duplicate`.

If the secret is absent, NuGet publishing is skipped without blocking GitHub Release creation. Branch/manual dry runs can never publish NuGet because publishing is guarded to tag pushes only.

## Checksums

`checksums.txt` contains SHA256 entries for every distributed archive/package and is verified inside the workflow before artifact upload.

```bash
sha256sum -c checksums.txt
```

PowerShell example:

```powershell
Get-FileHash .\erp-doctor-win-x64.zip -Algorithm SHA256
```

## Safety checklist before tagging

Before a real release:

1. `main` CI is green.
2. A Release dry run for the intended version is green.
3. Self-contained Windows/Linux publishes pass.
4. PostgreSQL, Docker, Nginx, and Redis provider archives are present.
5. The standalone Linux binary discovers all four providers with expected check counts.
6. Every checksum verifies.
7. README/config examples contain no customer-specific secrets/data.
8. Every bundled provider is intentionally included.
9. Only then create/push the release tag.
