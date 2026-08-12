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

## What v0.1 checks

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
                  Console / JSON
```

Every diagnostic implements the small `IDiagnosticCheck` contract. The core does not know about SQL Server, IIS, or HTTP, so future providers can be added without turning the CLI into one giant script.

## Commands

```text
erp-doctor check
erp-doctor system
erp-doctor sql
erp-doctor http
erp-doctor iis
```

Common options:

```text
--config <path>   JSON configuration file
--json <path>     Export results and diagnoses as JSON
--help            Show help
```

## Safety model

Production support tools should be boring and predictable.

ERP Doctor v0.1 follows these rules:

1. Diagnostics are read-only.
2. No automatic database repair or shrink.
3. No automatic SQL session kill.
4. No automatic IIS restart.
5. Configuration supports environment-variable secrets.
6. A diagnosis is presented as evidence-backed guidance, not absolute certainty.

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

### Next
- [ ] HTML report
- [ ] Sanitized support bundle
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
