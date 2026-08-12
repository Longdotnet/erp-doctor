# Releasing ERP Doctor

ERP Doctor uses `.github/workflows/release.yml` for repeatable packaging and releases.

## Outputs

A successful packaging run creates:

```text
erp-doctor-win-x64.zip
erp-doctor-linux-x64.tar.gz
erp-doctor-plugin-postgres.zip
erp-doctor-plugin-docker.zip
ErpDoctor.Tool.<version>.nupkg
ErpDoctor.PluginSdk.<version>.nupkg
checksums.txt
```

The Windows and Linux archives are self-contained .NET 8 builds, so the target machine does not need the .NET runtime installed.

PostgreSQL and Docker providers are shipped as separate plugin bundles because plugins remain an explicit trust/install boundary.

## Dry run

The Release workflow supports `workflow_dispatch` for manual packaging validation. Maintainer automation can also use a branch named `agent/release-dry-run-*`; branch dry runs can package/upload artifacts but are explicitly prevented from creating a GitHub Release or publishing NuGet packages.

Run a manual dry run with a SemVer such as:

```text
0.11.0-dev.1
```

A dry run:

- restores, builds, and tests the solution,
- packs the global tool and Plugin SDK,
- publishes self-contained `win-x64` and `linux-x64` builds,
- publishes the PostgreSQL and Docker plugin directories,
- runs the standalone Linux binary,
- verifies the standalone binary can discover both external plugin DLLs,
- creates ZIP/tar archives,
- creates and verifies SHA256 checksums,
- uploads the files as a workflow artifact,
- does **not** create a GitHub Release,
- does **not** publish to NuGet.org.

This is the required validation path before creating a release tag.

## Real release

A tag matching `v*.*.*` triggers the same pipeline and then creates a GitHub Release.

Example:

```bash
git tag v0.11.0
git push origin v0.11.0
```

The workflow validates the version after removing the leading `v`. Invalid version strings fail before packaging.

The release assets use stable names for platform/provider archives, while NuGet files retain their package version.

## Global tool package

The CLI is packed as:

```text
ErpDoctor.Tool
```

CI validates every change by packing a synthetic `0.0.0-ci` package, installing it into a temporary tool path, and running:

```bash
erp-doctor --help
```

This catches global-tool packaging errors before a release tag exists.

When the package has been published to NuGet.org, users can install it with:

```bash
dotnet tool install --global ErpDoctor.Tool
```

Upgrade with:

```bash
dotnet tool update --global ErpDoctor.Tool
```

## Provider bundles

Provider plugins are released separately from the standalone ERP Doctor binary.

Current provider archives:

```text
erp-doctor-plugin-postgres.zip
erp-doctor-plugin-docker.zip
```

Each provider archive contains its DLL/runtime dependencies when applicable, provider documentation, an example configuration, and the MIT license.

The release smoke test does not execute PostgreSQL or Docker diagnostics against a live service. It verifies that the self-contained ERP Doctor binary can load both external provider assemblies and discover the expected check counts. Live service access remains an environment-specific runtime concern.

## NuGet.org publishing

GitHub Release creation does not depend on NuGet.org credentials.

If the repository has an Actions secret named:

```text
NUGET_API_KEY
```

the tag workflow also pushes:

```text
ErpDoctor.Tool.<version>.nupkg
ErpDoctor.PluginSdk.<version>.nupkg
```

to `https://api.nuget.org/v3/index.json` with `--skip-duplicate`.

If the secret is absent, the workflow prints a skip message and still completes the GitHub Release. The `.nupkg` files remain attached to the release for inspection/manual publishing.

## Checksums

`checksums.txt` is generated with SHA256 for every release package and verified inside the workflow before artifact upload.

Linux example:

```bash
sha256sum -c checksums.txt
```

PowerShell example for a single file:

```powershell
Get-FileHash .\erp-doctor-win-x64.zip -Algorithm SHA256
```

Compare the result with the corresponding line in `checksums.txt`.

## Version ownership

Project files contain a development/default version so local `dotnet pack` remains useful.

The release workflow passes:

```text
-p:Version=<tag version>
```

to build/pack/publish. The release tag is therefore the source of truth for published package versions.

## Safety checklist before tagging

Before a real release:

1. `main` CI must be green.
2. Run the Release workflow in dry-run mode for the intended version.
3. Confirm standalone Windows/Linux builds pass.
4. Confirm PostgreSQL and Docker plugin bundles are present.
5. Confirm the standalone Linux binary discovers both provider plugins.
6. Confirm every entry in `checksums.txt` verifies successfully.
7. Review README/config examples for secrets or customer-specific data.
8. Confirm every third-party/provider plugin shipped in the release is intentionally included.
9. Only then create/push the release tag.
