# ERP Doctor

> Diagnose boring enterprise applications before your users call you.

[![CI](https://github.com/Longdotnet/erp-doctor/actions/workflows/ci.yml/badge.svg)](https://github.com/Longdotnet/erp-doctor/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

ERP Doctor is an open-source, evidence-first diagnostic CLI for the stack small ERP teams end up owning all at once: **Windows, IIS, .NET APIs, SQL Server, PostgreSQL, disks, memory, HTTP endpoints, Event Log, and environment configuration**.

The core idea:

```text
CHECK -> COLLECT EVIDENCE -> CORRELATE -> DIAGNOSE -> RECOMMEND
```

Instead of opening SSMS, IIS Manager, Event Viewer, Task Manager, and a browser one by one:

```bash
erp-doctor check
```

## Why ERP Doctor?

A health endpoint can tell you that an API is down. It usually cannot tell you whether the real problem is a stopped AppPool, disk pressure, database blocking, config drift, a failed .NET startup, or a database that doubled in size over the last week.

ERP Doctor collects those signals into one run and keeps the evidence together so the developer can reason about the **whole system**, not one component at a time.

## What it can inspect

| Area | Diagnostics |
| --- | --- |
| System | Fixed-disk free space, memory, .NET runtime, OS |
| SQL Server | Connectivity, data/log size, largest tables, blocking, long-running requests |
| SQL growth | Local historical snapshots, data/log deltas, MB/day, table/row growth |
| PostgreSQL plugin | Connectivity, database size, long-running queries, blocking sessions |
| IIS | AppPool state, site state, bindings, root physical path |
| HTTP | Expected status code, timeout, latency threshold |
| Windows Event Log | Recent Critical/Error entries, optional Warning/provider filters |
| Configuration | Secret-safe JSON/appsettings drift between environments |
| Reporting | Console, stable JSON, standalone HTML |
| Support handoff | Sanitized ZIP bundle with JSON + HTML + manifest |
| Plugin SDK | Explicit local DLL discovery and contributed diagnostic checks |

## Install

### From source

Requirements:

- .NET 8 SDK
- Windows for IIS/Event Log diagnostics
- SQL Server access for SQL Server diagnostics
- PostgreSQL access only when using the optional PostgreSQL plugin

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

ERP Doctor is packaged as:

```text
ErpDoctor.Tool
```

CI verifies the package by installing it into a clean temporary tool path and executing `erp-doctor --help` on every change.

When the package is available on NuGet.org:

```bash
dotnet tool install --global ErpDoctor.Tool
```

Upgrade with:

```bash
dotnet tool update --global ErpDoctor.Tool
```

### Self-contained binaries

The release pipeline produces self-contained archives that do not require a preinstalled .NET runtime:

```text
erp-doctor-win-x64.zip
erp-doctor-linux-x64.tar.gz
```

Release assets also include:

```text
erp-doctor-plugin-postgres.zip
ErpDoctor.Tool.<version>.nupkg
ErpDoctor.PluginSdk.<version>.nupkg
checksums.txt
```

See [`docs/releasing.md`](docs/releasing.md) for packaging, dry-run, checksums, and NuGet publishing.

## First run

Copy the default config:

```powershell
Copy-Item samples/erp-doctor.example.json erp-doctor.json
```

Keep SQL Server credentials out of the file:

```powershell
$env:ERP_DB="Server=localhost;Database=ERP;Integrated Security=True;TrustServerCertificate=True"
```

Run everything configured in the file:

```bash
erp-doctor check --config erp-doctor.json
```

## Example

```text
ERP Doctor
────────────────────────────────────────────────────────────────
Health score: 48/100 | Overall: Critical

SYSTEM
────────────────────────────────────────────────────────────────
✓ .NET runtime                   .NET 8.0.18
✗ Disk space (C:\)               5.4 GB free of 120.0 GB

SQL
────────────────────────────────────────────────────────────────
✓ SQL Server connection          Connected to SQL01/ERP_PROD in 24 ms
! SQL Server blocking            2 blocked requests; longest wait 47.0s

IIS
────────────────────────────────────────────────────────────────
✗ IIS AppPool ErpApi             AppPool state: Stopped
✓ IIS Site ERP Site              Started; expected binding present

HTTP
────────────────────────────────────────────────────────────────
✗ ERP API                        HTTP 503 in 31 ms

EVENTLOG
────────────────────────────────────────────────────────────────
! ERP application errors         2 recent .NET/IIS errors

DIAGNOSIS
────────────────────────────────────────────────────────────────
CRITICAL: Application unavailable with critically low disk space

  -> Free disk space before attempting repeated restarts.
  -> Inspect application startup/Event Log evidence.
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

erp-doctor report       Generate a standalone HTML report
erp-doctor bundle       Generate a sanitized support ZIP
erp-doctor growth       Capture/compare local SQL Server growth history
erp-doctor config-diff  Compare two JSON/appsettings files safely

erp-doctor plugins      Discover configured plugins without running their checks
erp-doctor plugin       Run only contributed plugin checks
```

Common options:

```text
--config <path>   JSON configuration file
--json <path>     Write machine-readable diagnostic JSON
--html <path>     Write standalone HTML
--bundle <path>   Write sanitized support ZIP
--history <path>  Local JSON state for SQL Server growth history
--help            Show CLI help
```

## SQL Server diagnostics

Built-in SQL Server diagnostics include connectivity, database/data/log size, largest tables, blocking sessions, and long-running requests.

Database growth can be tracked without creating anything in the customer database:

```bash
erp-doctor growth --config erp-doctor.json
```

The first run creates a local baseline. Later runs can show:

```text
Database : SQL01/ERP_PROD
Current  : 19.80 GB total
Since    : 7.0 days
Data     : +3210.0 MB
Log      : +540.0 MB
Total    : +3750.0 MB
Rate     : +535.7 MB/day

Table growth
────────────────────────────────────────────────────────────────
[dbo].[AuditLog]          +2840.0 MB   rows +4,821,093
[dbo].[AttendanceDetail]   +610.0 MB   rows   +931,501
```

The history lives on the machine running ERP Doctor. No history table, trigger, stored procedure, or SQL Agent job is created in SQL Server.

See [`docs/database-growth.md`](docs/database-growth.md).

## PostgreSQL reference plugin

`ErpDoctor.Plugin.Postgres` is the first production-style reference provider built on the Plugin SDK. It uses Npgsql and contributes four read-only checks:

```text
plugin.postgres.connectivity
plugin.postgres.database-size
plugin.postgres.long-running
plugin.postgres.blocking
```

Build the plugin:

```bash
dotnet build ErpDoctor.sln --configuration Release
```

Set the PostgreSQL connection string in an environment variable:

```powershell
$env:ERP_DOCTOR_POSTGRES="Host=localhost;Port=5432;Database=erp;Username=erp_doctor;Password=..."
```

Configuration stores only the environment-variable name:

```json
{
  "plugins": {
    "assemblies": [
      "plugins/ErpDoctor.Plugin.Postgres/bin/Release/net8.0/ErpDoctor.Plugin.Postgres.dll"
    ],
    "settings": {
      "postgres": {
        "connectionStringEnvironmentVariable": "ERP_DOCTOR_POSTGRES",
        "databaseSizeWarningGb": 20,
        "longRunningWarningSeconds": 30,
        "blockingWarningSeconds": 10
      }
    }
  }
}
```

Discover without running database checks:

```bash
erp-doctor plugins --config samples/postgres-plugin.example.json
```

Run the provider:

```bash
erp-doctor plugin --config samples/postgres-plugin.example.json
```

The provider intentionally does **not** export SQL text from `pg_stat_activity`. It never calls `pg_terminate_backend` or `pg_cancel_backend`.

See [`docs/postgres-plugin.md`](docs/postgres-plugin.md).

## Configuration drift

Compare DEV/UAT/PROD/customer appsettings without pasting credentials into a spreadsheet or chat:

```bash
erp-doctor config-diff \
  --left appsettings.Development.json \
  --right appsettings.Production.json \
  --ignore "Logging,Serilog"
```

Sensitive paths such as connection strings, passwords, tokens, secrets, API/access/private keys, and authorization values are compared in memory but displayed only as redacted state such as `[SET]`.

See [`docs/config-drift.md`](docs/config-drift.md).

## IIS and Windows Event Log

The `iis` category can inspect AppPool state, IIS site state, expected bindings, and the root physical path without modifying IIS.

The `eventlog` category reads recent Windows Event Log entries through native Windows APIs. It can filter by provider, lookback period, max event count, and severity. Event messages are truncated and scrubbed for common password/token/API-key/Bearer fragments before entering reports.

See [`docs/iis-sites.md`](docs/iis-sites.md) and [`docs/windows-event-log.md`](docs/windows-event-log.md).

## Reports and support bundles

Generate HTML:

```bash
erp-doctor report --config erp-doctor.json
```

Generate one support ZIP:

```bash
erp-doctor bundle --config erp-doctor.json
```

The bundle contains:

```text
report.json
report.html
manifest.json
```

ERP Doctor sanitizes secret-like report evidence before writing the bundle. Operational identifiers such as host names, database names, URLs, and table names may remain, so a bundle should still be reviewed before sharing outside the organization.

See [`docs/report-schema.md`](docs/report-schema.md) and [`docs/support-bundle.md`](docs/support-bundle.md).

## Plugin SDK

The public plugin contract lives in `ErpDoctor.PluginSdk`, which intentionally does **not** reference `ErpDoctor.Core`.

```csharp
using ErpDoctor.PluginSdk;

public sealed class MyPlugin : IErpDoctorPlugin
{
    public string Id => "my-company";
    public string Name => "My Company Diagnostics";
    public string Version => "0.1.0";

    public IReadOnlyList<IPluginCheck> CreateChecks(PluginContext context) =>
        [new MyCheck()];
}
```

Configure an explicit local DLL:

```json
{
  "plugins": {
    "assemblies": ["plugins/MyCompany.Diagnostics.dll"],
    "settings": {
      "my-company": {}
    }
  }
}
```

Validate discovery without running contributed checks:

```bash
erp-doctor plugins --config erp-doctor.json
```

Plugin IDs are automatically namespaced:

```text
plugin.<plugin-id>.<check-id>
```

A compile-tested minimal example lives at [`samples/ErpDoctor.SamplePlugin`](samples/ErpDoctor.SamplePlugin).

See [`docs/plugin-sdk.md`](docs/plugin-sdk.md).

## Plugin trust boundary

**Plugins are executable code.** They run inside the ERP Doctor process with the same operating-system permissions as ERP Doctor.

The host therefore:

- loads only explicit local `.dll` paths,
- refuses plugin URLs,
- validates plugin/check IDs and API compatibility,
- converts discovery/load failures into normal diagnostics,
- suppresses raw exception messages from plugin checks.

ERP Doctor's built-in diagnostics are designed to be read-only. That guarantee cannot automatically be extended to arbitrary third-party plugin code. Only install plugins you trust.

## Release pipeline

The `Release` workflow has two modes:

- **manual dispatch**: packaging dry-run only; uploads workflow artifacts and never creates a release,
- **push tag `v*.*.*`**: runs the same validation, creates a GitHub Release, and optionally publishes NuGet packages when `NUGET_API_KEY` is configured.

Every real release runs restore → build → test before packaging. SHA256 checksums are generated for all distributed archives/packages.

See [`docs/releasing.md`](docs/releasing.md).

## Architecture

```text
                         ErpDoctor.Cli
                              |
             +----------------+----------------+
             |                                 |
       Built-in checks                    PluginHost
             |                                 |
 System / SQL / HTTP / IIS / EventLog      PluginSdk DLLs
             |                            /        |       \
             |                      Sample    PostgreSQL   Future
             |                                 |
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
          +-------------------+-------------------+
          |                   |                   |
       Console               JSON             Reporting
                                                  |
                                           HTML / Sanitizer
                                                  |
                                            Support Bundle
```

The diagnostic Core does not depend on SQL Server, IIS, HTTP, Event Log, PostgreSQL, or other plugin implementations.

## Safety model

Built-in production diagnostics follow these rules:

1. No automatic database repair, shrink, or ERP data mutation.
2. No automatic SQL Server session kill.
3. No automatic IIS AppPool/site restart or binding modification.
4. Windows Event Log is queried/rendered only; channels are never cleared or changed.
5. SQL Server growth history writes local ERP Doctor state only.
6. Config drift never prints or hashes sensitive values.
7. Support bundles sanitize secret-like evidence before serialization.
8. Checks that lack permission report `Error` instead of attempting privilege escalation.
9. Diagnoses are evidence-backed guidance, not claims of absolute certainty.
10. The PostgreSQL reference plugin only performs inspection queries and never terminates backends.
11. Third-party plugins are a separate trust boundary and must be reviewed by the user.

## Roadmap

Completed:

- [x] v0.1 diagnostic core + System/SQL Server/HTTP/IIS + diagnosis engine
- [x] v0.2 stable report schema + health score + standalone HTML
- [x] v0.3 sanitized support bundle
- [x] v0.4 SQL Server database/table growth history
- [x] v0.5 secret-safe configuration drift
- [x] v0.6 IIS site/binding/physical-path diagnostics
- [x] v0.7 Windows Event Log collector
- [x] v0.8 Plugin SDK + PluginHost + sample plugin
- [x] v0.9 PostgreSQL reference provider plugin
- [x] v0.10 release automation + global-tool package smoke test

Next:

- [ ] Docker diagnostics plugin
- [ ] Linux/Nginx provider
- [ ] Redis/RabbitMQ providers
- [ ] Broader cross-platform support
- [ ] Installer UX after release feedback

## Contributing

The best diagnostics come from real incidents.

A useful check should be safe to run in production, collect evidence instead of guessing, bound its timeouts/work, explain required permissions, avoid destructive automatic fixes, and remain useful without an AI API.

For external providers, prefer the Plugin SDK instead of adding provider-specific dependencies to Core.

## License

MIT
