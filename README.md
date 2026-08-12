# ERP Doctor

> Diagnose boring enterprise applications before your users call you.

[![CI](https://github.com/Longdotnet/erp-doctor/actions/workflows/ci.yml/badge.svg)](https://github.com/Longdotnet/erp-doctor/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

ERP Doctor is an open-source, evidence-first diagnostic CLI for the infrastructure small ERP teams end up owning all at once: **Windows, IIS, .NET APIs, SQL Server, PostgreSQL, Docker, HTTP endpoints, Event Log, disks, memory, and environment configuration**.

```text
CHECK -> COLLECT EVIDENCE -> CORRELATE -> DIAGNOSE -> RECOMMEND
```

Instead of opening SSMS, IIS Manager, Event Viewer, Task Manager, Docker tooling, and a browser one by one:

```bash
erp-doctor check
```

## Why ERP Doctor?

An endpoint can tell you an API is down. It usually cannot tell you whether the real issue is a stopped AppPool, disk pressure, SQL blocking, an unhealthy container, config drift, a failed .NET startup, or a database that doubled in size over the last week.

ERP Doctor keeps those signals in one run so support/developers can reason about the **whole system**, not one component at a time.

## What it can inspect

| Area | Diagnostics |
| --- | --- |
| System | Fixed-disk free space, memory, .NET runtime, OS |
| SQL Server | Connectivity, data/log size, largest tables, blocking, long-running requests |
| SQL growth | Local historical snapshots, size deltas, MB/day, table/row growth |
| PostgreSQL plugin | Connectivity, database size, long-running queries, blocking sessions |
| Docker plugin | Engine reachability/version, engine summary, container state/health, expected containers |
| IIS | AppPool state, site state, bindings, root physical path |
| HTTP | Expected status code, timeout, latency threshold |
| Windows Event Log | Recent Critical/Error entries, optional Warning/provider filters |
| Configuration | Secret-safe JSON/appsettings drift between environments |
| Reporting | Console, stable JSON, standalone HTML |
| Support handoff | Sanitized ZIP bundle with JSON + HTML + manifest |
| Plugin SDK | Explicit local DLL discovery and contributed diagnostic checks |

## Install

### From source

```bash
git clone https://github.com/Longdotnet/erp-doctor.git
cd erp-doctor
dotnet restore
dotnet build ErpDoctor.sln
```

Run directly:

```bash
dotnet run --project src/ErpDoctor.Cli -- check --config erp-doctor.json
```

### .NET global tool

ERP Doctor is packaged as `ErpDoctor.Tool`. CI installs the packed tool into a clean temporary path and runs `erp-doctor --help` on every change.

When the package is available on NuGet.org:

```bash
dotnet tool install --global ErpDoctor.Tool
```

### Self-contained release archives

The release pipeline creates:

```text
erp-doctor-win-x64.zip
erp-doctor-linux-x64.tar.gz
erp-doctor-plugin-postgres.zip
erp-doctor-plugin-docker.zip
ErpDoctor.Tool.<version>.nupkg
ErpDoctor.PluginSdk.<version>.nupkg
checksums.txt
```

Windows/Linux archives are self-contained; provider plugins are separate trust/install boundaries. See [`docs/releasing.md`](docs/releasing.md).

## First run

Copy the default config:

```powershell
Copy-Item samples/erp-doctor.example.json erp-doctor.json
```

Keep SQL Server credentials out of JSON:

```powershell
$env:ERP_DB="Server=localhost;Database=ERP;Integrated Security=True;TrustServerCertificate=True"
```

Run configured diagnostics:

```bash
erp-doctor check --config erp-doctor.json
```

## Example

```text
ERP Doctor
────────────────────────────────────────────────────────────────
Health score: 48/100 | Overall: Critical

SYSTEM
✓ .NET runtime                   .NET 8.0.18
✗ Disk space (C:\)               5.4 GB free of 120.0 GB

SQL
✓ SQL Server connection          Connected to SQL01/ERP_PROD in 24 ms
! SQL Server blocking            2 blocked requests; longest wait 47.0s

IIS
✗ IIS AppPool ErpApi             AppPool state: Stopped

HTTP
✗ ERP API                        HTTP 503 in 31 ms

EVENTLOG
! ERP application errors         2 recent .NET/IIS errors

DIAGNOSIS
CRITICAL: Application unavailable with critically low disk space
  -> Free disk space before repeated restarts.
  -> Inspect startup/Event Log evidence.
  -> Re-run erp-doctor check after the root cause is resolved.
```

## Commands

```text
erp-doctor check        Run all configured built-in + plugin diagnostics
erp-doctor system       System diagnostics only
erp-doctor sql          SQL Server diagnostics only
erp-doctor http         HTTP endpoint diagnostics only
erp-doctor iis          IIS diagnostics only
erp-doctor eventlog     Windows Event Log diagnostics only

erp-doctor report       Generate standalone HTML
erp-doctor bundle       Generate sanitized support ZIP
erp-doctor growth       Capture/compare local SQL Server growth history
erp-doctor config-diff  Compare JSON/appsettings safely

erp-doctor plugins      Discover configured plugins without running checks
erp-doctor plugin       Run contributed plugin checks only
```

## SQL Server growth

A one-time database size tells you size, not **what grew**:

```bash
erp-doctor growth --config erp-doctor.json
```

Later snapshots can show data/log/total deltas, MB/day rate, and table/row growth. History is local ERP Doctor state: no SQL history table, trigger, stored procedure, or Agent job is created.

See [`docs/database-growth.md`](docs/database-growth.md).

## PostgreSQL provider

`ErpDoctor.Plugin.Postgres` contributes:

```text
plugin.postgres.connectivity
plugin.postgres.database-size
plugin.postgres.long-running
plugin.postgres.blocking
```

The connection string is read from an environment variable; SQL text from `pg_stat_activity` is deliberately excluded from evidence. The provider never calls `pg_terminate_backend` or `pg_cancel_backend`.

```powershell
$env:ERP_DOCTOR_POSTGRES="Host=localhost;Port=5432;Database=erp;Username=erp_doctor;Password=..."
```

```bash
erp-doctor plugin --config samples/postgres-plugin.example.json
```

See [`docs/postgres-plugin.md`](docs/postgres-plugin.md).

## Docker provider (v0.11)

`ErpDoctor.Plugin.Docker` contributes:

```text
plugin.docker.engine
plugin.docker.info
plugin.docker.containers
```

It uses the installed Docker CLI with fixed read-only arguments. It does not invoke a shell and never starts/stops/restarts/removes containers.

Example config:

```json
{
  "plugins": {
    "assemblies": [
      "plugins/ErpDoctor.Plugin.Docker/bin/Release/net8.0/ErpDoctor.Plugin.Docker.dll"
    ],
    "settings": {
      "docker": {
        "dockerExecutable": "docker",
        "commandTimeoutSeconds": 10,
        "warnOnStoppedContainers": false,
        "maxContainerEvidence": 20,
        "expectedContainers": ["erp-api", "redis"]
      }
    }
  }
}
```

Run it:

```bash
erp-doctor plugin --config samples/docker-plugin.example.json
```

Container evidence is limited to **name/state/health**. The provider does not emit env vars, labels, commands, mounts, or raw Docker stderr. Missing/not-running expected containers, unhealthy health, or severe states are Critical; stopped containers are noisy only when explicitly enabled.

See [`docs/docker-plugin.md`](docs/docker-plugin.md).

## Configuration drift

Compare DEV/UAT/PROD/customer settings:

```bash
erp-doctor config-diff \
  --left appsettings.Development.json \
  --right appsettings.Production.json \
  --ignore "Logging,Serilog"
```

Connection strings, passwords, tokens, secrets, API/access/private keys, and authorization values are compared in memory but displayed only as redacted state such as `[SET]`.

See [`docs/config-drift.md`](docs/config-drift.md).

## Reports and support bundles

```bash
erp-doctor report --config erp-doctor.json
erp-doctor bundle --config erp-doctor.json
```

The support ZIP contains:

```text
report.json
report.html
manifest.json
```

Secret-like report evidence is sanitized before serialization. Operational identifiers may remain, so bundles should still be reviewed before sharing outside the organization.

See [`docs/report-schema.md`](docs/report-schema.md) and [`docs/support-bundle.md`](docs/support-bundle.md).

## Plugin SDK

The public contract lives in `ErpDoctor.PluginSdk`, which intentionally does not reference Core.

```csharp
public sealed class MyPlugin : IErpDoctorPlugin
{
    public string Id => "my-company";
    public string Name => "My Company Diagnostics";
    public string Version => "0.1.0";

    public IReadOnlyList<IPluginCheck> CreateChecks(PluginContext context) =>
        [new MyCheck()];
}
```

Plugin IDs become:

```text
plugin.<plugin-id>.<check-id>
```

ERP Doctor loads only explicit local DLLs, rejects plugin URLs, validates API/check IDs, and converts load failures into diagnostics. **Plugins are executable code** and run with ERP Doctor process permissions; only load plugins you trust.

See [`docs/plugin-sdk.md`](docs/plugin-sdk.md).

## Release validation

Every release runs restore → build → test → package before distribution. Dry runs additionally prove:

- self-contained Windows/Linux publish,
- standalone Linux `--help`,
- standalone loading of PostgreSQL and Docker plugin DLLs,
- provider check counts,
- release archive creation,
- SHA256 verification.

Branch/manual dry runs cannot create a GitHub Release or publish NuGet; publishing is guarded to real tag pushes only.

## Architecture

```text
                         ErpDoctor.Cli
                              |
             +----------------+----------------+
             |                                 |
       Built-in checks                    PluginHost
             |                                 |
 System / SQL / HTTP / IIS / EventLog      PluginSdk DLLs
             |                         /       |       \
             |                    Sample   PostgreSQL  Docker
             +----------------+----------------+
                              |
                       DiagnosticRunner
                              |
                       DiagnosticResult
                              |
                       DiagnosisEngine
                              |
                       DiagnosticReport
                              |
             Console / JSON / HTML / Support Bundle
```

## Safety model

Built-in/provider diagnostics currently follow these principles:

1. No automatic ERP/database repair or data mutation.
2. No automatic SQL Server session kill.
3. No automatic IIS restart/binding modification.
4. Event Log channels are never cleared/changed.
5. SQL growth history writes local state only.
6. Config drift never prints/hashes sensitive values.
7. PostgreSQL provider never terminates/cancels backends.
8. Docker provider never changes container/engine state.
9. Permission/CLI failures become diagnostics instead of privilege escalation.
10. Third-party plugins remain a separate executable-code trust boundary.

## Roadmap

Completed:

- [x] v0.1 Core + System/SQL Server/HTTP/IIS + diagnosis engine
- [x] v0.2 report schema + health score + HTML
- [x] v0.3 sanitized support bundle
- [x] v0.4 SQL Server growth history
- [x] v0.5 secret-safe config drift
- [x] v0.6 IIS site/binding/path diagnostics
- [x] v0.7 Windows Event Log
- [x] v0.8 Plugin SDK + PluginHost
- [x] v0.9 PostgreSQL provider
- [x] v0.10 release automation + package/single-file validation
- [x] v0.11 Docker diagnostics provider

Next:

- [ ] Linux/Nginx provider
- [ ] Redis/RabbitMQ providers
- [ ] Broader cross-platform system diagnostics
- [ ] Installer UX based on release feedback

## Contributing

The best checks come from real incidents. Useful diagnostics should be safe to run in production, collect evidence instead of guessing, bound their timeouts/output, explain required permissions, avoid destructive automatic fixes, and remain useful without an AI API.

For external providers, prefer the Plugin SDK instead of adding provider-specific dependencies to Core.

## License

MIT
