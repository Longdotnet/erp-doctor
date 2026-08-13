# ERP Doctor

> Diagnose boring enterprise applications before your users call you.

[![CI](https://github.com/Longdotnet/erp-doctor/actions/workflows/ci.yml/badge.svg)](https://github.com/Longdotnet/erp-doctor/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

ERP Doctor is an open-source, evidence-first diagnostic CLI for the infrastructure small ERP teams end up owning all at once: **Windows/Linux hosts, IIS/Nginx, .NET APIs, SQL Server, PostgreSQL, Redis, RabbitMQ, Docker, DNS/TCP dependencies, Event Log, disks, memory, CPU/load pressure, and environment configuration**.

```text
CHECK -> COLLECT EVIDENCE -> CORRELATE -> DIAGNOSE -> RECOMMEND
```

Instead of opening SSMS, IIS Manager, Event Viewer, Task Manager, broker/database consoles, Docker/Linux tooling, and a browser one by one:

```bash
erp-doctor check
```

## Why ERP Doctor?

A health endpoint can tell you an API is down. It usually cannot tell you whether the immediate failure boundary is a stopped AppPool, DNS failure, closed TCP port, host CPU pressure, Linux load, low disk, SQL blocking, Redis memory pressure, a RabbitMQ alarm/backlog, an unhealthy container, config drift, invalid Nginx config, or a failed .NET startup.

ERP Doctor keeps those signals in one run so developers/support can reason about the **whole system** instead of checking each component manually.

## What it can inspect

| Area | Diagnostics |
| --- | --- |
| System | Fixed-disk space, memory, .NET/OS, Windows/Linux CPU, Linux load average, bounded top-process working sets |
| Network | Cross-platform DNS resolution and TCP reachability/latency for explicitly configured dependencies |
| SQL Server | Connectivity, data/log size, largest tables, blocking, long-running requests |
| SQL growth | Local historical snapshots, size deltas, MB/day, table/row growth |
| PostgreSQL plugin | Connectivity, database size, long-running queries, blocking sessions |
| Redis plugin | Connectivity, server metadata, memory pressure, persistence, replication health |
| RabbitMQ plugin | Management overview, node alarms/partitions, queue backlog/unacked/consumer health |
| Docker plugin | Engine reachability/version, engine summary, container state/health, expected containers |
| Nginx plugin | Nginx version and configuration validation |
| IIS | AppPool state, site state, bindings, root physical path |
| HTTP | Expected status, timeout, latency threshold |
| Windows Event Log | Recent Critical/Error entries, optional Warning/provider filters |
| Configuration | Secret-safe JSON/appsettings drift between environments |
| Reporting | Console, stable file/stdout JSON, standalone HTML |
| MCP | Optional read-only stdio server over the same versioned `DiagnosticReport` contract |
| Support handoff | Sanitized ZIP bundle with JSON + HTML + manifest |
| Plugin SDK | Explicit local DLL discovery and contributed diagnostic checks |

## Install

ERP Doctor supports source/global-tool workflows plus checksum-verified self-contained installers for **Windows x64** and **Linux x64**.

### Self-contained installer

The installer scripts consume a platform release archive and `checksums.txt`, verify SHA256 **before extraction**, then install the self-contained binary. A .NET SDK/runtime is not required on the target machine.

The first public GitHub Release has **not** been created yet. After a release exists, download/review the matching `install.ps1` or `install.sh` release asset and run it.

Windows PowerShell / PowerShell 7:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Linux:

```bash
bash install.sh
```

Default locations:

```text
Windows: %LOCALAPPDATA%\Programs\erp-doctor
Linux:   ~/.local/bin
```

Windows updates the current user's PATH unless `-NoPathUpdate` is supplied. Linux deliberately does not edit shell startup files. Both scripts also accept local archive/checksum paths for offline use and deterministic CI.

See [`docs/installing.md`](docs/installing.md).

### From source

```bash
git clone https://github.com/Longdotnet/erp-doctor.git
cd erp-doctor
dotnet restore
dotnet build ErpDoctor.sln
```

```bash
dotnet run --project src/ErpDoctor.Cli -- check --config erp-doctor.json
```

### .NET global tool

ERP Doctor is packaged as `ErpDoctor.Tool`. CI packs and installs the tool into a clean temporary path on every change.

When published to NuGet.org:

```bash
dotnet tool install --global ErpDoctor.Tool
```

## First run

Copy the example config:

```powershell
Copy-Item samples/erp-doctor.example.json erp-doctor.json
```

Keep credentials out of JSON. For SQL Server, for example:

```powershell
$env:ERP_DB="Server=localhost;Database=ERP;Integrated Security=True;TrustServerCertificate=True"
```

Then run:

```bash
erp-doctor check --config erp-doctor.json
```

## Commands

```text
erp-doctor check        Run all configured built-in + plugin diagnostics
erp-doctor system       Host disk/memory/CPU/load/process diagnostics
erp-doctor network      DNS + TCP diagnostics only
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

## Read-only MCP stdio server (v0.19)

ERP Doctor now has an optional MCP adapter that uses the **same built-in diagnostic catalog, runner, diagnosis engine, and `DiagnosticReport` schema** as the CLI.

Start the server with a local operator-owned config:

```bash
erp-doctor-mcp --config /path/to/erp-doctor.json
```

The v0.19 server supports **stdio only**. It opens no HTTP/TCP listener and exposes one tool:

```text
run_diagnostics
```

Allowed scopes:

```text
check
system
sql
http
network
iis
eventlog
plugin
```

The tool is annotated read-only/non-destructive/idempotent and returns structured `DiagnosticReport` schema `1.0` content. The MCP client can choose only the bounded scope; it **cannot choose a config path or plugin path per request**. `--config` is controlled by the operator when the server starts.

The server exposes no repair/restart/session-kill/process-kill/shell/SQL/file mutation tool. Stdio stdout is reserved for MCP protocol frames; logs go to stderr.

Windows/Linux MCP binaries are packaged as separate self-contained release assets rather than being silently installed with the normal CLI:

```text
erp-doctor-mcp-win-x64.zip
erp-doctor-mcp-linux-x64.tar.gz
```

Release dry-runs use the official C# `McpClient` to handshake with the **published Linux MCP binary**, list `run_diagnostics`, call it, and verify structured schema `1.0` results.

See [`docs/mcp-server.md`](docs/mcp-server.md).

## Machine-readable JSON stdout (v0.18)

Use `-` as the JSON destination when another program should consume ERP Doctor directly:

```bash
erp-doctor check --config erp-doctor.json --json -
```

This writes **one compact schema `1.0` JSON document to stdout** and suppresses the human console report. The same mode works with `report`, `system`, `sql`, `http`, `network`, `iis`, `eventlog`, and `plugin`.

Linux example:

```bash
erp-doctor system --json - | jq '.overallStatus, .healthScore'
```

PowerShell:

```powershell
$report = erp-doctor system --json - | ConvertFrom-Json
$report.schemaVersion
$report.results
```

The stdout/stderr boundary is intentional: report JSON stays on stdout, while usage/configuration failures go to stderr. Exit code `1` can still accompany a valid JSON document when diagnostics find a Critical/Error result.

To keep stdout deterministic, `--json -` rejects combinations with `--html` or `--bundle` before running diagnostics or creating those artifacts.

See [`docs/json-stdout.md`](docs/json-stdout.md) and [`docs/report-schema.md`](docs/report-schema.md).

## System Doctor (v0.17)

```bash
erp-doctor system --config erp-doctor.json
```

Built-in host pressure includes:

```text
system.cpu        Aggregate CPU utilization (Windows + Linux)
system.load       Linux 1/5/15-minute load average normalized per CPU
system.processes  Bounded top processes by working-set memory
```

Example thresholds:

```json
{
  "system": {
    "diskWarningFreePercent": 15,
    "diskCriticalFreePercent": 5,
    "memoryWarningAvailablePercent": 15,
    "cpuWarningPercent": 80,
    "cpuCriticalPercent": 95,
    "cpuSampleMilliseconds": 250,
    "loadPerCpuWarning": 1.0,
    "loadPerCpuCritical": 2.0,
    "topProcessesLimit": 5
  }
}
```

CPU sampling uses `GetSystemTimes` on Windows and `/proc/stat` on Linux. Linux load uses `/proc/loadavg`. No `top`, `ps`, WMI shell command, or external monitoring dependency is required.

The process snapshot intentionally retains only **PID + process name + working-set MB**. ERP Doctor does not collect process command lines, environment variables, memory contents, or open-file contents.

When host CPU is elevated while an HTTP endpoint is slow, the diagnosis engine can surface CPU pressure as a possible contributing factor. It remains correlation, not proof, and recommendations explicitly ask for sustained/repeated evidence before remediation.

See [`docs/system-pressure.md`](docs/system-pressure.md).

## Network Doctor (v0.16)

Each configured network target contributes a DNS check plus a TCP-connectivity check:

```json
{
  "network": {
    "targets": [
      {
        "name": "ERP SQL",
        "host": "${ERP_DB_HOST}",
        "port": 1433,
        "timeoutSeconds": 5,
        "latencyWarningMs": 1000,
        "maxResolvedAddresses": 5
      }
    ]
  }
}
```

```bash
erp-doctor network --config erp-doctor.json
```

Network Doctor uses .NET DNS/TCP APIs directly. It does not discover neighboring hosts, scan ports, send application payloads, modify DNS/firewall/routing, or copy raw socket exceptions into evidence.

If an HTTP endpoint is unavailable and the matching host/port TCP check also fails, ERP Doctor can correlate the outage below the HTTP application layer.

See [`docs/network-diagnostics.md`](docs/network-diagnostics.md).

## SQL Server growth

```bash
erp-doctor growth --config erp-doctor.json
```

Later snapshots can show data/log/total deltas, MB/day rate, and table/row growth. History is local ERP Doctor state: no SQL history table, trigger, stored procedure, or Agent job is created.

See [`docs/database-growth.md`](docs/database-growth.md).

## Configuration drift

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

The versioned `DiagnosticReport` is also available directly on stdout with `--json -` and as MCP structured content. Operational identifiers may remain in reports, so output/bundles should still be reviewed before sharing outside the organization.

See [`docs/report-schema.md`](docs/report-schema.md), [`docs/json-stdout.md`](docs/json-stdout.md), [`docs/mcp-server.md`](docs/mcp-server.md), and [`docs/support-bundle.md`](docs/support-bundle.md).

## Provider plugins

Provider dependencies stay outside Core and are loaded only from explicit local DLL paths.

### PostgreSQL

`ErpDoctor.Plugin.Postgres` contributes connectivity, database-size, long-running-query, and blocking checks. SQL text from `pg_stat_activity` is excluded from evidence; the provider never terminates/cancels backends.

```bash
erp-doctor plugin --config samples/postgres-plugin.example.json
```

See [`docs/postgres-plugin.md`](docs/postgres-plugin.md).

### Docker

`ErpDoctor.Plugin.Docker` uses fixed Docker CLI arguments without invoking a shell and never starts/stops/restarts/removes containers. Evidence is limited to bounded engine/container health metadata.

```bash
erp-doctor plugin --config samples/docker-plugin.example.json
```

See [`docs/docker-plugin.md`](docs/docker-plugin.md).

### Nginx

Starting in v0.17, generic Linux host pressure belongs to built-in System Doctor. `ErpDoctor.Plugin.Nginx` contributes only:

```text
plugin.nginx.version
plugin.nginx.config
```

It uses bounded version/config-validation commands, never `nginx -T`, and never reloads/stops Nginx.

```bash
erp-doctor plugin --config samples/nginx-plugin.example.json
```

See [`docs/nginx-plugin.md`](docs/nginx-plugin.md).

### Redis

`ErpDoctor.Plugin.Redis` uses fixed `redis-cli` arguments and only executes `PING` plus selected `INFO` sections. It does **not** inspect keys/values or run `KEYS`, `SCAN`, `GET`, `CONFIG`, or `MONITOR`.

Passwords stay out of JSON/process arguments and are passed to the child process through `REDISCLI_AUTH`.

```bash
erp-doctor plugin --config samples/redis-plugin.example.json
```

See [`docs/redis-plugin.md`](docs/redis-plugin.md).

### RabbitMQ

`ErpDoctor.Plugin.RabbitMq` uses the Management HTTP API with **GET-only** requests for overview, node alarms/partitions, and a bounded paginated queue list. Passwords come from an environment variable and failed response bodies are not copied into evidence.

```bash
erp-doctor plugin --config samples/rabbitmq-plugin.example.json
```

See [`docs/rabbitmq-plugin.md`](docs/rabbitmq-plugin.md).

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

Plugin IDs become `plugin.<plugin-id>.<check-id>`. ERP Doctor loads only explicit local DLLs, rejects plugin URLs, validates IDs, and converts load failures into diagnostics.

**Plugins are executable code** and run with ERP Doctor process permissions. Only load plugins you trust.

See [`docs/plugin-sdk.md`](docs/plugin-sdk.md).

## Release validation

Every release runs restore → build → test → package. Dry-runs additionally prove:

- self-contained CLI Windows/Linux publish,
- self-contained MCP server Windows/Linux publish,
- standalone Linux `--help`,
- standalone Linux Network Doctor DNS/TCP loopback behavior,
- standalone Linux System Doctor CPU/load/process-pressure execution,
- standalone Linux JSON stdout parsing/schema/no-human-output/conflict behavior,
- official-client MCP handshake/tool discovery/call against the **published Linux MCP binary**,
- standalone provider loading: PostgreSQL (4), Docker (3), Nginx (2), Redis (5), RabbitMQ (3),
- CLI/MCP/provider archive creation,
- SHA256 verification for platform/MCP/provider/NuGet/installer assets,
- Linux installer installation/execution from the packaged CLI release.

Windows CI separately validates the development MCP stdio handshake, packaged-tool JSON stdout, the PowerShell installer, invalid-checksum rejection, and valid archive installation/execution.

Branch/manual dry-runs cannot create a GitHub Release or publish NuGet; publishing is guarded to real tag pushes only.

See [`docs/releasing.md`](docs/releasing.md).

## Architecture

```text
                   ErpDoctor.Cli          ErpDoctor.Mcp
                        |                       |
                        +-----------+-----------+
                                    |
                    BuiltInDiagnosticCheckCatalog
                                    |
                +-------------------+-------------------+
                |                                       |
          Built-in checks                           PluginHost
                |                                       |
 System / Network / SQL / HTTP / IIS / EventLog      PluginSdk DLLs
                |                      /      /       |       |        \
                |                 PostgreSQL Docker  Nginx   Redis  RabbitMQ
                +-------------------+-------------------+
                                    |
                             DiagnosticRunner
                                    |
                             DiagnosticResult
                                    |
                             DiagnosisEngine
                                    |
                         DiagnosticReport (schema 1.0)
                                    |
       Console / JSON / HTML / Support Bundle / MCP structured content
```

## Safety model

Current built-in/provider/installer/MCP behavior follows these principles:

1. No automatic ERP/database repair or data mutation.
2. No automatic SQL Server session kill.
3. No automatic IIS restart/binding modification.
4. Event Log channels are never cleared/changed.
5. SQL growth history writes local state only.
6. Config drift never prints/hashes sensitive values.
7. Network Doctor only probes explicitly configured DNS names/TCP ports; it performs no discovery/scanning or network mutation.
8. System pressure checks never terminate/suspend processes and never collect process command lines, environment variables, or memory contents.
9. JSON stdout is serialization only: it opens no listener/server and grants no repair capability.
10. MCP v0.19 is stdio-only, exposes one read-only diagnostic tool, and does not let a client choose config/plugin paths per request.
11. PostgreSQL provider never terminates/cancels backends.
12. Redis provider never reads keys/values or changes Redis state/topology.
13. RabbitMQ provider is GET-only and never publishes/purges/deletes/requeues messages or mutates broker topology/accounts.
14. Docker provider never changes container/engine state.
15. Nginx provider never reloads/stops Nginx or dumps the full config.
16. Permission/CLI/API failures become diagnostics instead of privilege escalation.
17. Self-contained installers verify SHA256 before extraction; Windows does not clear custom destination directories and Linux does not modify shell startup files.
18. Third-party plugins remain a separate executable-code trust boundary.

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
- [x] v0.11 Docker provider
- [x] v0.12 Linux/Nginx provider
- [x] v0.13 Redis provider
- [x] v0.14 RabbitMQ provider
- [x] v0.15 checksum-verified Windows/Linux installer UX
- [x] v0.16 cross-platform DNS/TCP Network Doctor
- [x] v0.17 cross-platform CPU/load/process-pressure System Doctor
- [x] v0.18 machine-readable DiagnosticReport JSON stdout transport
- [x] v0.19 read-only MCP stdio server over the shared DiagnosticReport engine

Next:

- [ ] First public release/tag after explicit maintainer approval
- [ ] Additional providers/features driven by real incidents and contributor feedback
- [ ] Consider remote MCP transport only after an explicit authentication/authorization threat model

## Contributing

The best checks come from real incidents. Useful diagnostics should be safe to run in production, collect evidence instead of guessing, bound timeouts/output, explain required permissions, avoid destructive automatic fixes, and remain useful without an AI API.

For external providers, prefer the Plugin SDK instead of adding provider-specific dependencies to Core.

## License

MIT
