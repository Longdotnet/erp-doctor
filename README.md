# ERP Doctor

> Diagnose boring enterprise applications before your users call you.

ERP Doctor is a read-only diagnostic CLI for the stack that small ERP teams end up owning all at once: Windows servers, IIS, .NET APIs, SQL Server, disks, memory, and health endpoints.

The goal is simple:

```text
CHECK -> COLLECT EVIDENCE -> CORRELATE -> DIAGNOSE -> RECOMMEND
```

Instead of opening SSMS, IIS Manager, Event Viewer, Task Manager, and a browser one by one, run one command:

```bash
erp-doctor check
```

## What v0.3 checks

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
- Cross-check correlation for likely root causes
- Sanitized support-bundle export for support handoff

ERP Doctor is intentionally **read-only**. It does not kill SQL sessions, shrink databases, restart IIS, delete logs, or update ERP data.

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

## Configuration

See [`samples/erp-doctor.example.json`](samples/erp-doctor.example.json).

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
    "appPools": ["ErpApi"]
  }
}
```

## Architecture

```text
                    ErpDoctor.Cli
                         |
                    DiagnosticRunner
                         |
        +----------------+----------------+
        |                |                |
      System           SQL Server       HTTP/IIS
        |                |                |
        +----------------+----------------+
                         |
                  DiagnosticResult
                         |
                   DiagnosisEngine
                         |
                  DiagnosticReport
                         |
          +--------------+----------------------+
          |              |                      |
       Console          JSON            ErpDoctor.Reporting
                                             |       |
                                            HTML   Sanitizer
                                                     |
                                               Support Bundle
                                                     |
                                      report.json / report.html / manifest.json
```

Every diagnostic implements the small `IDiagnosticCheck` contract. The core does not know about SQL Server, IIS, or HTTP, so future providers can be added without turning the CLI into one giant script.

## Commands

```text
erp-doctor check
erp-doctor report
erp-doctor bundle
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
--help            Show help
```

## Safety model

Production support tools should be boring and predictable.

ERP Doctor v0.3 follows these rules:

1. Diagnostics are read-only.
2. No automatic database repair or shrink.
3. No automatic SQL session kill.
4. No automatic IIS restart.
5. Configuration supports environment-variable secrets.
6. Support bundles sanitize secret-like report data before writing output.
7. The source configuration file is not included in support bundles.
8. A diagnosis is presented as evidence-backed guidance, not absolute certainty.

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

### Next
- [ ] Database growth history
- [ ] Configuration drift comparison
- [ ] IIS site/binding diagnostics
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
