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
erp-doctor-plugin-rabbitmq.zip
install.ps1
install.sh
ErpDoctor.Tool.<version>.nupkg
ErpDoctor.PluginSdk.<version>.nupkg
checksums.txt
```

The Windows and Linux archives are self-contained .NET 8 builds. PostgreSQL, Docker, Linux/Nginx, Redis, and RabbitMQ are shipped as separate provider bundles because plugins remain an explicit trust/install boundary.

`install.ps1` and `install.sh` are release assets too. Both scripts verify the platform archive SHA256 before extraction, and both installer files are themselves included in `checksums.txt`.

## Dry run

The Release workflow supports `workflow_dispatch` for manual packaging validation. Maintainer automation can also use a branch named `agent/release-dry-run-*`; branch dry runs package/upload artifacts but are explicitly prevented from creating a GitHub Release or publishing NuGet packages.

Use a development SemVer such as:

```text
0.16.0-dev.1
```

A dry run:

- restores, builds, and tests the solution,
- packs the global tool and Plugin SDK,
- publishes self-contained `win-x64` and `linux-x64` builds,
- publishes PostgreSQL, Docker, Linux/Nginx, Redis, and RabbitMQ provider bundles,
- runs the standalone Linux binary,
- verifies standalone Linux Network Doctor DNS/TCP behavior against a loopback listener,
- verifies that the standalone binary can discover all bundled provider DLLs with expected check counts,
- packages `install.ps1` and `install.sh`,
- creates ZIP/tar archives,
- generates and verifies SHA256 checksums for every distributed archive/package/installer,
- runs the Linux installer against the packaged Linux archive and executes the installed binary,
- uploads the files as a workflow artifact,
- does **not** create a GitHub Release,
- does **not** publish to NuGet.org.

Normal Windows CI additionally runs the installer under Windows PowerShell, verifies that an intentionally bad checksum is rejected, installs from a valid local archive/checksum pair, and runs the installed `erp-doctor.exe --help`.

This is the required validation path before creating a release tag.

## Real release

A tag matching `v*.*.*` triggers the same release pipeline and then creates a GitHub Release.

Example:

```bash
git tag v0.16.0
git push origin v0.16.0
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
erp-doctor-plugin-rabbitmq.zip
```

Each archive contains its provider DLL/runtime dependencies when applicable, provider documentation, example configuration, and MIT license.

Release smoke testing verifies the self-contained ERP Doctor binary can load:

- PostgreSQL: 4 checks,
- Docker: 3 checks,
- Linux/Nginx: 3 checks,
- Redis: 5 checks,
- RabbitMQ: 3 checks.

The provider smoke test validates discovery/loading only. It does not execute provider diagnostics against live services. Live-service permissions, authentication, management endpoints, and availability remain deployment-specific runtime concerns.

## Network Doctor validation

The Linux release dry-run also starts a loopback-only temporary listener and executes:

```bash
erp-doctor network --config artifacts/network-smoke.json
```

The configured target uses `127.0.0.1` and a fixed CI-only port. The command must complete successfully, proving that the self-contained Linux artifact can perform both built-in DNS and TCP diagnostics without shell/network-tool dependencies.

The listener exists only for the workflow step and is terminated automatically when the step exits.

## Installer validation

The installer scripts support both normal GitHub Release downloads and local archive/checksum inputs used by CI.

Windows CI validates:

- Windows PowerShell compatibility,
- help/syntax path,
- invalid SHA256 rejection,
- valid SHA256 acceptance,
- extraction/copy into an isolated install directory,
- execution of the installed binary.

Release dry-run validates Linux with the actual self-contained release tarball and the generated release `checksums.txt`.

Installer validation does not mutate the runner's global machine configuration. Windows uses `-NoPathUpdate` in CI; Linux uses an isolated install directory.

See [`installing.md`](installing.md).

## NuGet.org publishing

If the repository has an Actions secret named `NUGET_API_KEY`, a real tag release additionally pushes:

```text
ErpDoctor.Tool.<version>.nupkg
ErpDoctor.PluginSdk.<version>.nupkg
```

to NuGet.org with `--skip-duplicate`.

If the secret is absent, NuGet publishing is skipped without blocking GitHub Release creation. Branch/manual dry runs can never publish NuGet because publishing is guarded to tag pushes only.

## Checksums

`checksums.txt` contains SHA256 entries for every distributed archive/package/installer and is verified inside the workflow before artifact upload.

```bash
sha256sum -c checksums.txt
```

PowerShell example:

```powershell
Get-FileHash .\erp-doctor-win-x64.zip -Algorithm SHA256
```

## Safety checklist before tagging

Before a real release:

1. `main` CI is green, including Windows installer validation.
2. A Release dry run for the intended version is green, including Linux installer and Network Doctor loopback validation.
3. Self-contained Windows/Linux publishes pass.
4. PostgreSQL, Docker, Nginx, Redis, and RabbitMQ provider archives are present.
5. The standalone Linux binary discovers all five providers with expected check counts.
6. The standalone Linux binary passes the DNS/TCP Network Doctor loopback smoke.
7. `install.ps1`, `install.sh`, and every other release asset have entries in `checksums.txt`.
8. Every checksum verifies.
9. README/config/install examples contain no customer-specific secrets/data.
10. Every bundled provider is intentionally included.
11. Only then create/push the release tag.
