# ERP Doctor

> Diagnose boring enterprise applications before your users call you.

[![CI](https://github.com/Longdotnet/erp-doctor/actions/workflows/ci.yml/badge.svg)](https://github.com/Longdotnet/erp-doctor/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

ERP Doctor is an open-source, evidence-first diagnostic CLI for the infrastructure small ERP teams end up owning all at once: **Windows, Linux, IIS, Nginx, .NET APIs, SQL Server, PostgreSQL, Redis, RabbitMQ, Docker, HTTP endpoints, Event Log, disks, memory, and environment configuration**.

```text
CHECK -> COLLECT EVIDENCE -> CORRELATE -> DIAGNOSE -> RECOMMEND
```

Instead of opening SSMS, IIS Manager, Event Viewer, Task Manager, broker/database consoles, Docker/Linux tooling, and a browser one by one:

```bash
erp-doctor check
```

## Why ERP Doctor?

An endpoint can tell you an API is down. It usually cannot tell you whether the real issue is a stopped AppPool, Linux load pressure, invalid Nginx config, disk pressure, SQL blocking, Redis memory pressure, a RabbitMQ node alarm or queue backlog, an unhealthy container, config drift, or a failed .NET startup.

ERP Doctor keeps those signals in one run so support/developers can reason about the **whole system**, not one component at a time.

## What it can inspect

| Area | Diagnostics |
| --- | --- |
| System | Fixed-disk free space, memory, .NET runtime, OS |
| SQL Server | Connectivity, data/log size, largest tables, blocking, long-running requests |
| SQL growth | Local historical snapshots, size deltas, MB/day, table/row growth |
| PostgreSQL plugin | Connectivity, database size, long-running queries, blocking sessions |
| Redis plugin | Connectivity, server metadata, memory pressure, persistence, replication health |
| RabbitMQ plugin | Management API overview, node resource alarms/partitions, queue backlog/unacked/consumer health |
| Docker plugin | Engine reachability/version, engine summary, container state/health, expected containers |
| Linux/Nginx plugin | Linux uptime/load/memory snapshot, Nginx version, config validation |
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
erp-doctor-plugin-nginx.zip
erp-doctor-plugin-redis.zip
erp-doctor-plugin-rabbitmq.zip
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

```bash
erp-doctor growth --config erp-doctor.json
```

Later snapshots can show data/log/total deltas, MB/day rate, and table/row growth. History is local ERP Doctor state: no SQL history table, trigger, stored procedure, or Agent job is created.

See [`docs/database-growth.md`](docs/database-growth.md).

## Provider plugins

Provider dependencies stay outside Core and are loaded only from explicit local DLL paths.

### PostgreSQL

`ErpDoctor.Plugin.Postgres` contributes connectivity, database-size, long-running-query, and blocking checks. The connection string comes from an environment variable; SQL text from `pg_stat_activity` is excluded from evidence. The provider never terminates/cancels backends.

```bash
erp-doctor plugin --config samples/postgres-plugin.example.json
```

See [`docs/postgres-plugin.md`](docs/postgres-plugin.md).

### Redis

`ErpDoctor.Plugin.Redis` uses `redis-cli` with fixed argument lists and only executes `PING` plus selected `INFO` sections. It does **not** inspect keys/values or run `KEYS`, `SCAN`, `GET`, `CONFIG`, or `MONITOR`.

Passwords stay out of JSON and process arguments; the child process receives the configured secret through `REDISCLI_AUTH` and diagnostic evidence excludes raw stderr/authentication material.

```bash
erp-doctor plugin --config samples/redis-plugin.example.json
```

See [`docs/redis-plugin.md`](docs/redis-plugin.md).

### RabbitMQ (v0.14)

`ErpDoctor.Plugin.RabbitMq` contributes:

```text
plugin.rabbitmq.overview
plugin.rabbitmq.nodes
plugin.rabbitmq.queues
```

It uses the RabbitMQ Management HTTP API with **GET-only** requests to overview, nodes, and a paginated queue list. Passwords are resolved from an environment variable and the Basic Authorization header is created only in memory.

```powershell
$env:ERP_DOCTOR_RABBITMQ_PASSWORD="your-secret"
```

```bash
erp-doctor plugin --config samples/rabbitmq-plugin.example.json
```

Node checks treat down nodes, memory/disk alarms, and network partitions as Critical. Queue checks evaluate ready/unacknowledged backlog thresholds and can optionally warn on ready messages with zero consumers. Queue scans are bounded to one page with `maxQueues` hard-capped at 500.

The provider does not export definitions, retrieve/requeue message payloads, publish, purge/delete queues, mutate topology/users/permissions/policies, or close connections. Failed HTTP response bodies and Authorization data are not copied into evidence.

See [`docs/rabbitmq-plugin.md`](docs/rabbitmq-plugin.md).

### Docker

`ErpDoctor.Plugin.Docker` uses fixed Docker CLI arguments without invoking a shell and never starts/stops/restarts/removes containers. Container evidence is limited to **name/state/health**; env vars, labels, commands, mounts, and raw stderr are excluded.

```bash
erp-doctor plugin --config samples/docker-plugin.example.json
```

See [`docs/docker-plugin.md`](docs/docker-plugin.md).

### Linux / Nginx

`ErpDoctor.Plugin.Nginx` reads Linux runtime evidence from `/etc/os-release` and `/proc`, then uses only bounded Nginx version/config validation commands. It never dumps the full Nginx config and never reloads/stops Nginx.

```bash
erp-doctor plugin --config samples/nginx-plugin.example.json
```

See [`docs/nginx-plugin.md`](docs/nginx-plugin.md).

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

Plugin IDs become `plugin.<plugin-id>.<check-id>`. ERP Doctor loads only explicit local DLLs, rejects plugin URLs, validates API/check IDs, and converts load failures into diagnostics.

**Plugins are executable code** and run with ERP Doctor process permissions; only load plugins you trust.

See [`docs/plugin-sdk.md`](docs/plugin-sdk.md).

## Release validation

Every release runs restore → build → test → package. Dry runs additionally prove:

- self-contained Windows/Linux publish,
- standalone Linux `--help`,
- standalone loading of PostgreSQL (4), Docker (3), Nginx (3), Redis (5), and RabbitMQ (3) checks,
- provider archive creation,
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
             |              /       /        |        |       |        \
             |         Sample  PostgreSQL  Redis  RabbitMQ  Docker   Nginx
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

Current built-in/provider diagnostics follow these principles:

1. No automatic ERP/database repair or data mutation.
2. No automatic SQL Server session kill.
3. No automatic IIS restart/binding modification.
4. Event Log channels are never cleared/changed.
5. SQL growth history writes local state only.
6. Config drift never prints/hashes sensitive values.
7. PostgreSQL provider never terminates/cancels backends.
8. Redis provider never reads keys/values or changes Redis state/topology.
9. RabbitMQ provider is GET-only and never publishes/purges/deletes/requeues messages or mutates broker topology/accounts.
10. Docker provider never changes container/engine state.
11. Nginx provider never reloads/stops Nginx or dumps the full config.
12. Permission/CLI/API failures become diagnostics instead of privilege escalation.
13. Third-party plugins remain a separate executable-code trust boundary.

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

Next:

- [ ] Broader cross-platform system diagnostics
- [ ] Installer UX based on release feedback
- [ ] Optional machine-readable integration/MCP surface after provider feedback
- [ ] Additional providers driven by real incidents/contributor demand

## Contributing

The best checks come from real incidents. Useful diagnostics should be safe to run in production, collect evidence instead of guessing, bound timeouts/output, explain required permissions, avoid destructive automatic fixes, and remain useful without an AI API.

For external providers, prefer the Plugin SDK instead of adding provider-specific dependencies to Core.

## License

MIT
