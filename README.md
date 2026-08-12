# ERP Doctor

> Diagnose boring enterprise applications before your users call you.

[![CI](https://github.com/Longdotnet/erp-doctor/actions/workflows/ci.yml/badge.svg)](https://github.com/Longdotnet/erp-doctor/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

ERP Doctor is an open-source, evidence-first diagnostic CLI for the stack small ERP teams end up owning all at once: **Windows, IIS, .NET APIs, SQL Server, disks, memory, HTTP endpoints, Event Log, configuration drift, and now external diagnostic plugins**.

The core idea is simple:

```text
CHECK -> COLLECT EVIDENCE -> CORRELATE -> DIAGNOSE -> RECOMMEND
```

Instead of opening SSMS, IIS Manager, Event Viewer, Task Manager, and a browser one by one:

```bash
erp-doctor check
```

## Why ERP Doctor?

Traditional health checks usually answer one question: "is this endpoint alive?"

ERP incidents are rarely that simple. A 503 may come from a stopped AppPool, critically low disk space, SQL blocking, configuration drift, or a failed .NET startup recorded in Event Log.

ERP Doctor collects those signals into one run and keeps the evidence together so the developer can reason about the **whole system**, not one component at a time.

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

## What it can inspect

| Area | Diagnostics |
| --- | --- |
| System | Fixed-disk free space, memory, .NET runtime, OS |
| SQL Server | Connectivity, data/log size, largest tables, blocking, long-running requests |
| Database growth | Local historical snapshots, data/log deltas, MB/day, table/row growth |
| IIS | AppPool state, site state, bindings, root physical path |
| HTTP | Expected status code, timeout, latency threshold |
| Windows Event Log | Recent Critical/Error events, optional Warning/provider filters |
| Configuration | Secret-safe JSON/appsettings drift between environments |
| Reporting | Console, stable JSON, standalone HTML |
| Support handoff | Sanitized ZIP bundle with JSON + HTML + manifest |
| Plugins | Explicit local DLL discovery and contributed diagnostic checks |

## Getting started from source

Requirements:

- .NET 8 SDK
- Windows for IIS/Event Log diagnostics
- SQL Server access for SQL diagnostics

```bash
git clone https://github.com/Longdotnet/erp-doctor.git
cd erp-doctor
dotnet restore
dotnet build ErpDoctor.sln
```

Copy the example config:

```powershell
Copy-Item samples/erp-doctor.example.json erp-doctor.json
```

Keep secrets out of the file. The example reads SQL credentials from an environment variable:

```powershell
$env:ERP_DB="Server=localhost;Database=ERP;Integrated Security=True;TrustServerCertificate=True"
```

Run all configured diagnostics:

```bash
dotnet run --project src/ErpDoctor.Cli -- check --config erp-doctor.json
```

## Configuration

A compact example:

```json
{
  "sqlServer": {
    "connectionString": "${ERP_DB}",
    "blockingWarningSeconds": 10,
    "longRunningWarningSeconds": 30
  },
  "http": {
    "endpoints": [
      {
        "name": "ERP API",
        "url": "https://localhost:5001/health",
        "expectedStatusCode": 200,
        "latencyWarningMs": 1500
      }
    ]
  },
  "iis": {
    "appPools": ["ErpApi"],
    "sites": [
      {
        "name": "ERP Site",
        "expectedBindings": ["https:*:443:erp.example.com"]
      }
    ]
  },
  "windowsEventLog": {
    "queries": [
      {
        "name": "ERP application errors",
        "logName": "Application",
        "lookbackMinutes": 60,
        "maxEvents": 20,
        "providers": [".NET Runtime", "Application Error", "IIS AspNetCore Module V2"]
      }
    ]
  },
  "plugins": {
    "assemblies": [],
    "settings": {}
  }
}
```

See [`samples/erp-doctor.example.json`](samples/erp-doctor.example.json) for all current options.

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
erp-doctor growth       Capture/compare local SQL growth history
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
--history <path>  Local JSON state for growth history
--help            Show CLI help
```

## Database growth history

A one-time database size tells you the size, not **what grew**.

```bash
erp-doctor growth --config erp-doctor.json
```

First run creates a local baseline. Later runs report:

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

The history lives on the machine running ERP Doctor. No table, trigger, stored procedure, or SQL Agent job is created in the customer database.

See [`docs/database-growth.md`](docs/database-growth.md).

## Configuration drift

Compare DEV/UAT/PROD/customer appsettings:

```bash
erp-doctor config-diff \
  --left appsettings.Development.json \
  --right appsettings.Production.json \
  --ignore "Logging,Serilog"
```

Sensitive paths such as connection strings, passwords, tokens, secrets, API/access/private keys, and authorization values are compared in memory but displayed only as redacted state such as `[SET]`.

See [`docs/config-drift.md`](docs/config-drift.md).

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

## Plugin SDK (v0.8)

ERP Doctor can now accept checks without changing Core or the CLI.

The public contract lives in **`ErpDoctor.PluginSdk`**, which intentionally does **not** reference `ErpDoctor.Core`.

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
    "assemblies": [
      "plugins/ErpDoctor.Plugin.Postgres.dll"
    ],
    "settings": {
      "postgres": {
        "connectionStringEnvironmentVariable": "POSTGRES_DB"
      }
    }
  }
}
```

Validate discovery without executing plugin checks:

```bash
erp-doctor plugins --config erp-doctor.json
```

Run only plugin checks:

```bash
erp-doctor plugin --config erp-doctor.json
```

Plugin check IDs are automatically namespaced as:

```text
plugin.<plugin-id>.<check-id>
```

A compile-tested sample is included at [`samples/ErpDoctor.SamplePlugin`](samples/ErpDoctor.SamplePlugin).

See [`docs/plugin-sdk.md`](docs/plugin-sdk.md) for the API, dependency loading, configuration, failure behavior, and trust model.

## Plugin trust boundary

**Plugins are executable code.** They run inside the ERP Doctor process with the same OS permissions as ERP Doctor.

The host therefore loads only explicit local DLL paths, refuses URLs, checks API compatibility, converts load failures into diagnostics, and suppresses raw exception messages from plugin checks.

ERP Doctor's built-in diagnostics are designed to be read-only. That guarantee cannot automatically be extended to arbitrary third-party plugin code. Only install plugins you trust.

## Architecture

```text
                         ErpDoctor.Cli
                              |
             +----------------+----------------+
             |                                 |
       Built-in checks                    PluginHost
             |                                 |
 System / SQL / HTTP / IIS / EventLog      PluginSdk DLLs
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

The diagnostic Core does not depend on SQL Server, IIS, HTTP, Event Log, or plugin implementations.

## Safety model

Built-in production diagnostics follow these rules:

1. No automatic database repair, shrink, or data mutation.
2. No automatic SQL session kill.
3. No automatic IIS AppPool/site restart or binding modification.
4. Event Log is queried/rendered only; channels are never cleared or changed.
5. Growth history writes local ERP Doctor state only.
6. Config drift never prints or hashes sensitive values.
7. Support bundles sanitize secret-like evidence before serialization.
8. Checks that lack permission report `Error` instead of attempting privilege escalation.
9. Diagnoses are evidence-backed guidance, not claims of absolute certainty.
10. Third-party plugins are a separate trust boundary and must be reviewed by the user.

## Roadmap

Completed:

- [x] v0.1 diagnostic core + System/SQL/HTTP/IIS + diagnosis engine
- [x] v0.2 stable report schema + health score + standalone HTML
- [x] v0.3 sanitized support bundle
- [x] v0.4 SQL database/table growth history
- [x] v0.5 secret-safe configuration drift
- [x] v0.6 IIS site/binding/physical-path diagnostics
- [x] v0.7 Windows Event Log collector
- [x] v0.8 Plugin SDK + PluginHost + sample plugin

Next:

- [ ] PostgreSQL reference provider plugin
- [ ] Docker diagnostics
- [ ] Linux/Nginx provider
- [ ] Redis/RabbitMQ providers
- [ ] Release automation and simple binary/global-tool installation
- [ ] Broader cross-platform support

## Contributing

The best diagnostics come from real incidents.

A useful check should be safe to run in production, collect evidence instead of guessing, bound its timeouts/work, explain required permissions, avoid destructive automatic fixes, and remain useful without an AI API.

For external providers, prefer the Plugin SDK instead of adding provider-specific dependencies to Core.

## License

MIT
