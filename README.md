# ERP Doctor

> Diagnose boring enterprise applications before your users call you.

ERP Doctor is a read-only diagnostic CLI for the stack that small ERP teams end up owning all at once: Windows servers, IIS, .NET APIs, SQL Server, disks, memory, health endpoints, and environment configuration.

The goal is simple:

```text
CHECK -> COLLECT EVIDENCE -> CORRELATE -> DIAGNOSE -> RECOMMEND
```

Instead of opening SSMS, IIS Manager, Event Viewer, Task Manager, and a browser one by one, run one command:

```bash
erp-doctor check
```

## What v0.6 checks

- Fixed-disk free space
- System memory
- .NET runtime and OS information
- SQL Server connectivity
- SQL Server database/data/log size
- Largest SQL Server tables
- SQL Server blocking sessions
- Long-running SQL Server requests
- HTTP endpoint status and latency
- IIS application-pool state
- IIS site state, bindings, and physical path
- Cross-check correlation for likely root causes
- Sanitized support-bundle export for support handoff
- SQL database/table growth compared with a local historical baseline
- Secret-safe JSON/appsettings configuration drift between environments

ERP Doctor is intentionally **read-only toward your ERP stack**. It does not kill SQL sessions, shrink databases, restart IIS, modify IIS bindings, delete logs, update ERP data, or rewrite appsettings files. Features such as `growth` may write ERP Doctor's own local state files.

## Example

```text
ERP Doctor
────────────────────────────────────────────────────────────────

SYSTEM
────────────────────────────────────────────────────────────────
✓ .NET runtime                   .NET 8.0.18
! Disk space (C:\)               5.4 GB free of 120.0 GB (4.5% free)

SQL
────────────────────────────────────────────────────────────────
✓ SQL Server connection          Connected to SQL01/ERP_PROD in 24 ms
! SQL Server blocking            2 blocked request(s); longest wait 47.0s
i SQL Server largest tables      Top table: [dbo].[AuditLog]: 18,291,922 rows

IIS
────────────────────────────────────────────────────────────────
✗ IIS AppPool ErpApi             AppPool state: Stopped
✗ IIS Site ERP Site              1 expected binding(s) missing

HTTP
────────────────────────────────────────────────────────────────
✗ ERP API                        HTTP 503 in 31 ms

DIAGNOSIS
────────────────────────────────────────────────────────────────
CRITICAL: Application unavailable with critically low disk space

The API is unavailable while an IIS application pool is stopped and
the server has critically low disk space.

  -> Free disk space before attempting repeated restarts.
  -> Inspect Windows Event Viewer and application startup logs.
  -> Re-run erp-doctor check after the root cause is resolved.
```

## Getting started from source

Requirements:

- .NET 8 SDK
- Windows when using IIS diagnostics
- Access to the SQL Server you want to inspect

```bash
git clone https://github.com/Longdotnet/erp-doctor.git
cd erp-doctor
dotnet restore
dotnet build
```

Copy the example configuration:

```powershell
Copy-Item samples/erp-doctor.example.json erp-doctor.json
```

Keep secrets out of the config file. The example reads the SQL connection string from an environment variable:

```powershell
$env:ERP_DB="Server=localhost;Database=ERP;Integrated Security=True;TrustServerCertificate=True"
```

Run all diagnostics:

```bash
dotnet run --project src/ErpDoctor.Cli -- check --config erp-doctor.json
```

Run only one category:

```bash
dotnet run --project src/ErpDoctor.Cli -- system
dotnet run --project src/ErpDoctor.Cli -- sql --config erp-doctor.json
dotnet run --project src/ErpDoctor.Cli -- http --config erp-doctor.json
dotnet run --project src/ErpDoctor.Cli -- iis --config erp-doctor.json
```

Export machine-readable evidence:

```bash
dotnet run --project src/ErpDoctor.Cli -- check --config erp-doctor.json --json report.json
```

Generate a standalone HTML report that can be opened or sent to another developer without a server:

```bash
dotnet run --project src/ErpDoctor.Cli -- report --config erp-doctor.json
```

By default, `report` writes `erp-doctor-report.html`. You can choose explicit outputs:

```bash
dotnet run --project src/ErpDoctor.Cli -- check \
  --config erp-doctor.json \
  --json report.json \
  --html report.html
```

The JSON export uses a stable report envelope containing a schema version, generated timestamp, overall status, health score, summary counts, raw diagnostic results, and correlated diagnoses. See [`docs/report-schema.md`](docs/report-schema.md) for the contract and scoring rules.

## Sanitized support bundle

Generate one ZIP for a support handoff:

```bash
dotnet run --project src/ErpDoctor.Cli -- bundle --config erp-doctor.json
```

The default output is `erp-doctor-support.zip`. You can also generate it while running `check`:

```bash
dotnet run --project src/ErpDoctor.Cli -- check \
  --config erp-doctor.json \
  --bundle artifacts/customer-a.support.zip
```

Every support bundle contains:

```text
report.json
report.html
manifest.json
```

ERP Doctor sanitizes secret-like evidence **before** JSON serialization and HTML rendering. It never copies the source configuration file into the bundle. Host names, database names, URLs, table names, machine information, and other troubleshooting evidence can still remain, so review a bundle before sending it outside your organization.

See [`docs/support-bundle.md`](docs/support-bundle.md) for the sanitization boundary and file contract.

## Database growth history

A one-time database size cannot tell you what caused a database to jump from 8 GB to 20 GB. Capture a local baseline instead:

```bash
dotnet run --project src/ErpDoctor.Cli -- growth --config erp-doctor.json
```

The first run creates `erp-doctor-growth.json`. Later runs compare against the most recent snapshot for the same server/database and show data/log/total deltas, an MB/day rate when meaningful, and table-size/row-count changes.

```text
ERP Doctor - Database Growth
────────────────────────────────────────────────────────────────────────
Database : SQL01/ERP_PROD
Current  : 19.80 GB total (14.70 GB data, 5.10 GB log)
Since    : 2026-08-05 03:00:00 UTC (7.0 days)
Data     : +3210.0 MB
Log      : +540.0 MB
Total    : +3750.0 MB
Rate     : +535.7 MB/day

Table growth
────────────────────────────────────────────────────────────────────────
  [dbo].[AuditLog]                      +2840.0 MB  rows   +4,821,093
  [dbo].[AttendanceDetail]               +610.0 MB  rows     +931,501
```

Choose a separate history file per environment/customer when useful:

```bash
erp-doctor growth --config customer-a.json --history history/customer-a.json
```

The history file stores size metadata only; the SQL connection string is not persisted. ERP Doctor does not create any SQL history table, trigger, stored procedure, or Agent job. See [`docs/database-growth.md`](docs/database-growth.md).

## Configuration drift

Compare DEV/UAT/PROD/customer appsettings without copying values into a spreadsheet or accidentally pasting credentials into chat:

```bash
erp-doctor config-diff \
  --left appsettings.Development.json \
  --right appsettings.Production.json
```

Example:

```text
ERP Doctor - Configuration Drift
────────────────────────────────────────────────────────────────────────
Drift : 3 difference(s)

~ Api:BaseUrl (different)
  left  : https://dev.example.test
  right : https://prod.example.test

- FeatureFlags:NewCheckout (only on left)
  left  : true
  right : [MISSING]

~ ConnectionStrings:ERP (different)
  left  : [SET]
  right : [SET]
  note  : sensitive values are redacted; ERP Doctor does not hash or print them.
```

Ignore noisy sections when needed:

```bash
erp-doctor config-diff \
  --left appsettings.Development.json \
  --right appsettings.Production.json \
  --ignore "Logging,Serilog"
```

`config-diff` returns `0` when there is no drift, `1` when drift exists, and `2` for invalid input. Sensitive paths such as connection strings, passwords, tokens, secrets, API/access/private keys, and authorization values are compared in memory but never printed or hashed. Common inline credentials inside otherwise ordinary strings are redacted before display.

See [`docs/config-drift.md`](docs/config-drift.md) for exact comparison and redaction semantics.

## IIS sites and bindings

The `iis` command can validate the site itself, not only its AppPool:

```json
{
  "iis": {
    "appPools": ["ErpApi"],
    "sites": [
      {
        "name": "ERP Site",
        "expectedBindings": ["https:*:443:erp.example.com"],
        "checkPhysicalPath": true
      }
    ]
  }
}
```

For each configured site ERP Doctor checks whether the site is `Started`, whether its root physical path exists, and whether expected protocol/IP/port/host bindings are present. Extra live bindings remain evidence but do not fail the check. A missing site, missing required binding, stopped site, or missing required physical path is `Critical`.

The implementation reads the IIS `Microsoft.Web.Administration.dll` already installed on Windows through reflection, so no extra package is required. It never starts/stops sites or changes bindings. See [`docs/iis-sites.md`](docs/iis-sites.md).

## Configuration

See [`samples/erp-doctor.example.json`](samples/erp-doctor.example.json).

```json
{
  "sqlServer": {
    "connectionString": "${ERP_DB}",
    "blockingWarningSeconds": 10,
    "longRunningWarningSeconds": 30,
    "growthTablesLimit": 50
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
  }
}
```

## Architecture

```text
                         ErpDoctor.Cli
                              |
              +---------------+----------------+
              |               |                |
       DiagnosticRunner     growth        config-diff
              |               |                |
   +----------+---------+  SQL snapshot     JSON compare
   |          |         |      |                |
 System   SQL Server HTTP/IIS local history  secret-safe diff
   |          |         |      |
   +----------+---------+  delta analyzer
              |
       DiagnosticResult
              |
        DiagnosisEngine
              |
       DiagnosticReport
              |
   +----------+---------------------+
   |          |                     |
 Console     JSON           ErpDoctor.Reporting
                                  |       |
                                 HTML   Sanitizer
                                          |
                                    Support Bundle
```

Every diagnostic implements the small `IDiagnosticCheck` contract. The diagnostic core does not know about SQL Server, IIS, or HTTP, and local analysis commands remain isolated from production write operations.

## Commands

```text
erp-doctor check
erp-doctor report
erp-doctor bundle
erp-doctor growth
erp-doctor config-diff
erp-doctor system
erp-doctor sql
erp-doctor http
erp-doctor iis
```

Common options:

```text
--config <path>   JSON configuration file
--json <path>     Export the stable diagnostic report as JSON
--html <path>     Export a standalone HTML diagnostic report
--bundle <path>   Export a sanitized support ZIP
--history <path>  Local JSON state used by database growth history
--help            Show help
```

Configuration drift options:

```text
--left <path>     Left JSON/appsettings file
--right <path>    Right JSON/appsettings file
--ignore <paths>  Comma/semicolon-separated path prefixes to ignore
```

## Safety model

Production support tools should be boring and predictable.

ERP Doctor v0.6 follows these rules:

1. Diagnostics and growth queries are read-only toward the ERP/database.
2. No automatic database repair or shrink.
3. No automatic SQL session kill.
4. No automatic IIS site/AppPool restart or binding/path modification.
5. Configuration supports environment-variable secrets.
6. Support bundles sanitize secret-like report data before writing output.
7. The source configuration file is not included in support bundles.
8. Growth history is ERP Doctor local state and never creates objects in SQL Server.
9. Configuration drift reads local JSON only and never prints or hashes sensitive values.
10. IIS site checks inspect Windows/IIS state without rewriting IIS configuration.
11. A diagnosis is presented as evidence-backed guidance, not absolute certainty.

Some SQL dynamic management views require additional SQL Server permissions. If a check cannot run, ERP Doctor reports the check as an error instead of attempting privilege escalation.

## Roadmap

### v0.1
- [x] Diagnostic core
- [x] System checks
- [x] SQL Server checks
- [x] HTTP checks
- [x] IIS AppPool check
- [x] Diagnosis rule engine
- [x] JSON report
- [x] CI and tests

### v0.2
- [x] Stable report schema
- [x] Health score and overall status
- [x] Standalone HTML report
- [x] HTML-encoded evidence and suggestions
- [x] `erp-doctor report` command
- [x] Report tests

### v0.3
- [x] `erp-doctor bundle` command
- [x] Sanitized report copy before serialization
- [x] ZIP with JSON, HTML, and manifest
- [x] Secret-like evidence redaction
- [x] Support-bundle regression tests

### v0.4
- [x] `erp-doctor growth` command
- [x] Local versioned database-size history
- [x] Data/log/total growth deltas and MB/day rate
- [x] Top table-size and row-count changes
- [x] Atomic history writes and snapshot retention
- [x] Growth analyzer/store regression tests

### v0.5
- [x] `erp-doctor config-diff` command
- [x] Case-insensitive nested JSON/appsettings comparison
- [x] Missing/different/type-changed classification
- [x] Secret-safe display without credential hashing
- [x] Ignore path prefixes
- [x] Config drift regression tests

### v0.6
- [x] IIS site-state diagnostics
- [x] IIS binding evidence and expected-binding validation
- [x] IIS root physical-path validation
- [x] Dependency-free `Microsoft.Web.Administration` inspection
- [x] IIS site evaluator regression tests

### Next
- [ ] Windows Event Log collector
- [ ] Plugin SDK
- [ ] PostgreSQL, Linux/Nginx, Docker, Redis providers

## Contributing

The most useful contributions are diagnostics born from real incidents.

A good diagnostic should:

- be safe to run in production,
- collect evidence instead of guessing,
- explain required permissions,
- avoid destructive fixes,
- return actionable suggestions,
- remain useful without an AI API.

Open an issue with the incident you want ERP Doctor to detect before implementing a large provider.

## License

MIT
