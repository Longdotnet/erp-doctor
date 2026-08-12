# Releasing ERP Doctor

ERP Doctor v0.10 uses `.github/workflows/release.yml` for repeatable packaging and releases.

## Outputs

A successful packaging run creates:

```text
erp-doctor-win-x64.zip
erp-doctor-linux-x64.tar.gz
erp-doctor-plugin-postgres.zip
ErpDoctor.Tool.<version>.nupkg
ErpDoctor.PluginSdk.<version>.nupkg
checksums.txt
```

The Windows and Linux archives are self-contained .NET 8 builds, so the target machine does not need the .NET runtime installed.

The PostgreSQL plugin is shipped separately because provider plugins remain an explicit trust/install boundary.

## Dry run

The Release workflow supports `workflow_dispatch`.

Run it from GitHub Actions with a SemVer such as:

```text
0.10.0-dev.1
```

A manual run:

- restores, builds, and tests the solution,
- packs the global tool and Plugin SDK,
- publishes self-contained `win-x64` and `linux-x64` builds,
- publishes the PostgreSQL plugin directory,
- creates ZIP/tar archives,
- creates SHA256 checksums,
- uploads the files as a workflow artifact,
- does **not** create a GitHub Release,
- does **not** publish to NuGet.org.

This is the required validation path before creating a release tag.

## Real release

A tag matching `v*.*.*` triggers the same pipeline and then creates a GitHub Release.

Example:

```bash
git tag v0.10.0
git push origin v0.10.0
```

The workflow validates the version after removing the leading `v`. Invalid version strings fail before packaging.

The release assets use stable names for the platform archives, while the NuGet files retain their package version.

## Global tool package

The CLI is packed as:

```text
ErpDoctor.Tool
```

CI validates every change by packing a synthetic `0.0.0-ci` package, installing it into a temporary tool path, and running:

```bash
erp-doctor --help
```

This catches packaging errors before a release tag exists.

When the package has been published to NuGet.org, users can install it with:

```bash
dotnet tool install --global ErpDoctor.Tool
```

Upgrade with:

```bash
dotnet tool update --global ErpDoctor.Tool
```

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

`checksums.txt` is generated with SHA256 for every release package.

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
2. Run the Release workflow manually in dry-run mode for the intended version.
3. Confirm the dry-run includes all expected archives, `.nupkg` files, and `checksums.txt`.
4. Review README/config examples for secrets or customer-specific data.
5. Confirm any third-party plugin shipped in the release is intentionally included.
6. Only then create/push the release tag.
