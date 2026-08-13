# Releasing ERP Doctor

ERP Doctor uses `.github/workflows/release.yml` for repeatable packaging and releases. v0.20 also adds `.github/workflows/report-diff-release-smoke.yml` as a focused Linux self-contained pre-release gate for the report regression feature.

## Outputs

A successful packaging run creates:

```text
erp-doctor-win-x64.zip
erp-doctor-linux-x64.tar.gz
erp-doctor-mcp-win-x64.zip
erp-doctor-mcp-linux-x64.tar.gz
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

The CLI and MCP Windows/Linux archives are self-contained .NET 8 builds. The MCP server is shipped separately so installing the normal CLI does not implicitly install or enable an MCP process.

PostgreSQL, Docker, Nginx, Redis, and RabbitMQ are separate provider bundles because plugins remain an explicit trust/install boundary.

`install.ps1` and `install.sh` install the normal CLI release archive, not the MCP server. Both verify the platform archive SHA256 before extraction, and both installer files are themselves included in `checksums.txt`.

## Dry run

The Release workflow supports `workflow_dispatch` for manual packaging validation. Maintainer automation can also use a branch named `agent/release-dry-run-*`; branch dry runs package/upload artifacts but are explicitly prevented from creating a GitHub Release or publishing NuGet packages.

For v0.20, the same `agent/release-dry-run-*` branch also triggers the focused **Report Diff Release Smoke** workflow. A v0.20 promotion therefore requires both the full Release dry run and the focused Linux report-diff gate to pass.

Use a development SemVer such as:

```text
0.20.0-dev.1
```

The full Release dry run:

- restores, builds, and tests the full solution including `ErpDoctor.Mcp`,
- packs the global tool and Plugin SDK,
- publishes self-contained CLI `win-x64` and `linux-x64` builds,
- publishes self-contained MCP server `win-x64` and `linux-x64` builds,
- publishes PostgreSQL, Docker, Nginx, Redis, and RabbitMQ provider bundles,
- runs the standalone Linux CLI,
- verifies standalone Linux Network Doctor DNS/TCP behavior against a loopback listener,
- verifies standalone Linux System Doctor CPU/load/process-pressure checks execute without runtime errors,
- verifies standalone Linux DiagnosticReport `--json -` output is one parseable schema `1.0` document with no human console leakage,
- uses the official C# `McpClient` to start the **published self-contained Linux MCP binary**, complete the stdio handshake, list `run_diagnostics`, call it with `scope=system`, and verify structured schema `1.0` results,
- verifies that the standalone CLI can discover all bundled provider DLLs with expected check counts,
- packages CLI/MCP/provider archives plus `install.ps1` and `install.sh`,
- generates and verifies SHA256 checksums for every distributed archive/package/installer,
- runs the Linux CLI installer against the packaged Linux CLI archive and executes the installed binary,
- uploads the files as a workflow artifact,
- does **not** create a GitHub Release,
- does **not** publish to NuGet.org.

The v0.20 Report Diff Release Smoke additionally:

- publishes a fresh self-contained Linux x64 CLI from the same dry-run commit,
- executes the published binary rather than the development assembly,
- compares a deterministic healthy baseline against a warning candidate,
- requires human output to report a failed regression gate and process exit code `1`,
- requires `--json -` to return one compact report-diff schema `1.0` document,
- verifies `hasRegression`, `regressionCount`, health-score delta, and per-check classification,
- reverses the reports and requires the recovery comparison to exit `0` with no regression.

Normal Windows CI additionally verifies the development MCP stdio server through the official C# `McpClient`, validates read-only tool annotations and bounded scope errors, installs the packed global CLI tool, parses DiagnosticReport JSON stdout, executes packaged `report-diff` regression/recovery cases, and runs checksum-verified installer gates.

These are the required validation paths before creating a release tag.

## Real release

A tag matching `v*.*.*` triggers the full release pipeline and then creates a GitHub Release.

Example after explicit maintainer approval:

```bash
git tag v0.20.0
git push origin v0.20.0
```

The release tag is the source of truth for published package versions. The workflow passes `-p:Version=<tag-version>` to build/pack/publish.

The focused v0.20 report-diff workflow is primarily a pre-release dry-run guard. Before tagging, use the exact commit intended for release on an `agent/release-dry-run-*` branch and require both workflows to be green.

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

## Diagnostic report diff validation

v0.20 adds:

```bash
erp-doctor report-diff --left before.json --right after.json
```

The deployment gate semantics are deliberate:

```text
exit 0  comparison succeeded, no regression
exit 1  comparison succeeded, regression found
exit 2  invalid/incompatible input
```

Windows CI validates the feature through the actually packed/installed global tool. The focused Linux release smoke validates the self-contained published executable.

Both platforms require a Healthy -> Warning change for `system.cpu` to produce:

```text
kind: regressed
hasRegression: true
regressionCount: 1
healthScoreDelta: -20
exit: 1
```

Reversing the same two reports must produce no regression and exit `0`.

The regression engine intentionally compares check status/coverage rather than volatile evidence or duration values. See [`report-diff.md`](report-diff.md).

## MCP server archives

The optional MCP server is released separately:

```text
erp-doctor-mcp-win-x64.zip
erp-doctor-mcp-linux-x64.tar.gz
```

Each archive contains the self-contained MCP executable, MIT license, MCP server documentation, and example ERP Doctor configuration.

The v0.19 server is stdio-only and exposes one read-only `run_diagnostics` tool. A client cannot supply config/plugin paths per request; the local operator owns `--config` at server startup.

See [`mcp-server.md`](mcp-server.md).

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

Release smoke testing verifies the self-contained CLI can load:

- PostgreSQL: 4 checks,
- Docker: 3 checks,
- Nginx: 2 checks,
- Redis: 5 checks,
- RabbitMQ: 3 checks.

The provider smoke validates discovery/loading only; it does not execute provider diagnostics against live services.

## Network Doctor validation

The Linux release dry-run starts a loopback-only temporary listener and executes:

```bash
erp-doctor network --config artifacts/network-smoke.json
```

The command must complete successfully, proving the self-contained Linux artifact can perform built-in DNS/TCP diagnostics without shell/network-tool dependencies.

## System pressure validation

The Linux release dry-run also executes:

```bash
erp-doctor system --config artifacts/system-pressure-smoke.json
```

The smoke requires:

```text
System CPU
System load average
Top processes by memory
```

and rejects an `Error` result for any of those checks. Warning/Critical is allowed because hosted-runner load is not deterministic.

## JSON stdout validation

The self-contained Linux CLI must produce one compact parseable DiagnosticReport schema `1.0` document for:

```bash
erp-doctor system --config artifacts/system-pressure-smoke.json --json -
```

The mixed-output guard must also reject `--json - --html` with usage exit code `2`, empty stdout, and no HTML artifact.

`report-diff --json -` is also deterministic JSON-only stdout, but returns the separate versioned report-diff schema.

See [`json-stdout.md`](json-stdout.md) and [`report-diff.md`](report-diff.md).

## MCP validation

The normal test suite starts the development `ErpDoctor.Mcp` server with the official C# `McpClient`, then verifies tool discovery and structured diagnostics.

The release dry-run repeats the handshake against the **published Linux MCP executable** by setting the test's server command to:

```text
artifacts/mcp-linux-x64/ErpDoctor.Mcp
```

The test requires:

- successful stdio initialization,
- discovery of exactly the intended `run_diagnostics` tool,
- `scope=system` tool invocation,
- non-error tool result,
- structured `schemaVersion == "1.0"`,
- non-empty diagnostic results.

This prevents a release from passing only because the development assembly works while the self-contained MCP binary is broken.

## Installer validation

The installer scripts support both normal GitHub Release downloads and local archive/checksum inputs used by CI.

Windows CI validates:

- Windows PowerShell compatibility,
- help/syntax path,
- invalid SHA256 rejection,
- valid SHA256 acceptance,
- extraction/copy into an isolated install directory,
- execution of the installed CLI binary.

Release dry-run validates Linux with the actual self-contained CLI release tarball and generated `checksums.txt`.

The CLI installer deliberately does not install the optional MCP server.

## NuGet.org publishing

If the repository has an Actions secret named `NUGET_API_KEY`, a real tag release additionally pushes:

```text
ErpDoctor.Tool.<version>.nupkg
ErpDoctor.PluginSdk.<version>.nupkg
```

to NuGet.org with `--skip-duplicate`.

If the secret is absent, NuGet publishing is skipped without blocking GitHub Release creation. Branch/manual dry runs can never publish NuGet because publishing is guarded to tag pushes only.

## Checksums

`checksums.txt` contains SHA256 entries for every distributed CLI/MCP/provider archive, package, and installer and is verified inside the workflow before artifact upload.

```bash
sha256sum -c checksums.txt
```

PowerShell example:

```powershell
Get-FileHash .\erp-doctor-mcp-win-x64.zip -Algorithm SHA256
```

## Safety checklist before tagging

Before a real release:

1. `main` CI is green, including MCP stdio, DiagnosticReport JSON stdout, packaged report-diff, and Windows installer validation.
2. A full Release dry run from the exact intended commit is green.
3. The v0.20 self-contained Linux Report Diff Release Smoke from that same commit is green.
4. Self-contained CLI Windows/Linux publishes pass.
5. Self-contained MCP Windows/Linux publishes pass.
6. The official MCP client successfully handshakes/calls the published Linux MCP binary and receives structured schema `1.0` diagnostics.
7. PostgreSQL, Docker, Nginx, Redis, and RabbitMQ provider archives are present and provider discovery counts match expectations.
8. Network Doctor, System Doctor, and DiagnosticReport JSON stdout Linux smokes pass.
9. Report-diff regression/recovery semantics pass on both the packaged Windows global tool and self-contained Linux binary.
10. CLI/MCP/provider/NuGet/installer assets all have entries in `checksums.txt`.
11. Every checksum verifies.
12. README/config/MCP/install/report-diff examples contain no customer-specific secrets/data.
13. Every bundled provider is intentionally included.
14. Only then create/push the release tag.
