# ERP Doctor MCP stdio server

ERP Doctor v0.19 adds an optional MCP server adapter over the existing diagnostic engine.

The server is intentionally small:

```text
MCP client
   |
   | stdio
   v
ErpDoctor.Mcp
   |
   v
BuiltInDiagnosticCheckCatalog + configured trusted plugins
   |
   v
DiagnosticRunner -> DiagnosisEngine -> DiagnosticReport schema 1.0
```

It does not implement a second diagnostic engine and it does not shell out to the CLI. The CLI and MCP server share the same built-in diagnostic check catalog.

## Transport

The v0.19 server supports **stdio only**.

It does not open an HTTP/TCP listener, expose a web endpoint, bind a network port, or run a remote daemon.

Start it with:

```bash
erp-doctor-mcp --config /path/to/erp-doctor.json
```

For a self-contained release asset, configure the MCP client with the downloaded executable as its command and pass:

```text
--config
/path/to/erp-doctor.json
```

as startup arguments.

MCP clients differ in how they represent a stdio command/arguments pair, so use the client's current documentation for the surrounding client configuration format.

## Tool

v0.19 intentionally exposes one tool:

```text
run_diagnostics
```

Input:

```json
{
  "scope": "system"
}
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

`check` runs the full configured diagnostic set and produces the normal evidence correlations/diagnoses. The other scopes run only that diagnostic category.

The tool returns the existing versioned `DiagnosticReport` as MCP structured content. Current report schema version:

```text
1.0
```

## Tool annotations

The tool explicitly declares:

```text
readOnly     = true
destructive  = false
idempotent   = true
openWorld    = true
```

`openWorld=true` is intentional because configured ERP Doctor checks may inspect external services such as HTTP endpoints, databases, brokers, containers, or network targets.

The tool remains read-only from ERP Doctor's side: external access is inspection only and no repair capability is exposed.

## Fixed startup configuration

The MCP client **cannot provide a config path in a tool call**.

The operator chooses the local config once when starting the MCP process:

```bash
erp-doctor-mcp --config /srv/erp-doctor/erp-doctor.json
```

Tool calls can choose only the bounded `scope` enum-like value.

This prevents a model/client from using the MCP tool to probe arbitrary local configuration files or dynamically point ERP Doctor at unrelated configs.

An explicitly supplied config path must exist. URLs are rejected. Unknown startup arguments are rejected with usage exit code `2`.

When no `--config` argument is supplied, ERP Doctor uses the normal default path `erp-doctor.json`; if it does not exist, the existing default built-in configuration behavior applies.

## Stdout/stderr boundary

MCP stdio protocol messages use stdout. ERP Doctor therefore treats stdout as protocol-only while the server is running.

Server logging is configured to stderr so log text cannot corrupt MCP frames.

`--help` also writes its usage line to stderr and exits without starting the server.

## Plugin trust boundary

The `plugin` scope and full `check` scope can load plugin assemblies that the **operator already configured** in `erp-doctor.json`.

Plugins are executable .NET code and run with the ERP Doctor process permissions. The MCP client cannot add a plugin path or change plugin configuration in a tool call, but an operator must still load only trusted plugin assemblies.

Provider plugins bundled by this repository keep their existing read-only constraints. Third-party plugin behavior belongs to the third-party plugin author.

## Error boundary

Invalid `scope` values become a bounded MCP validation error:

```text
Scope must be one of: check, system, sql, http, network, iis, eventlog, plugin.
```

The error deliberately does not echo filesystem paths, configuration contents, or raw internal exception details.

Existing individual diagnostics continue converting expected permission/connectivity failures into bounded diagnostic results rather than privilege escalation.

## What the MCP server cannot do

v0.19 exposes no MCP tool to:

- restart IIS/Nginx/application services,
- kill SQL/PostgreSQL sessions,
- terminate processes,
- clear Event Logs,
- modify DNS/firewall/routes,
- publish/purge/delete RabbitMQ messages/queues,
- change Redis keys/configuration,
- start/stop/remove Docker containers,
- modify ERP/database data,
- edit local configuration,
- choose arbitrary config/plugin paths per request.

There is no generic shell/SQL/file tool in the MCP server.

## SDK/version choice

v0.19 pins the official C# MCP package:

```text
ModelContextProtocol 1.4.1
```

The project intentionally uses a stable package rather than a preview major version for the production-facing adapter.

## Validation

Normal test runs use the official C# `McpClient` to:

1. start `ErpDoctor.Mcp` over stdio,
2. complete the MCP handshake,
3. list tools,
4. verify `run_diagnostics` is discoverable,
5. call it with `scope=system`,
6. verify structured content contains `schemaVersion=1.0` and diagnostic results.

Release dry-runs additionally perform the same official-client handshake against the **self-contained Linux MCP release binary**, not only the development assembly.

The Windows/Linux MCP binaries are packaged as separate release assets so installing the normal ERP Doctor CLI does not implicitly enable or install an MCP server.

## Relationship to `--json -`

v0.18 JSON stdout and v0.19 MCP are complementary:

- `erp-doctor ... --json -` is a generic process integration transport for scripts/CI.
- `ErpDoctor.Mcp` is an MCP protocol adapter using the same diagnostic models directly.

The MCP server does not parse CLI presentation text or spawn the CLI process.
