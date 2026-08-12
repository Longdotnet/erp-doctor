# PostgreSQL reference plugin

`ErpDoctor.Plugin.Postgres` is the first real provider built on the ERP Doctor Plugin SDK. Its purpose is both operational and architectural: it adds useful PostgreSQL diagnostics while proving that an external provider can remain outside `ErpDoctor.Core` and the CLI.

The plugin targets .NET 8 and uses Npgsql 10.0.3.

## Checks

The plugin contributes four checks:

| Check ID | What it inspects | Status |
| --- | --- | --- |
| `connectivity` | Opens PostgreSQL and reads current database/server version | Healthy or Error |
| `database-size` | `pg_database_size(current_database())` | Healthy or Warning at configured threshold |
| `long-running` | Active sessions in `pg_stat_activity` older than threshold | Healthy or Warning |
| `blocking` | Sessions with `pg_blocking_pids(pid)` older than threshold | Healthy or Warning |

Once loaded by ERP Doctor, the final IDs are namespaced automatically:

```text
plugin.postgres.connectivity
plugin.postgres.database-size
plugin.postgres.long-running
plugin.postgres.blocking
```

## Build

Build the solution in Release mode:

```bash
dotnet build ErpDoctor.sln --configuration Release
```

The plugin DLL is produced at:

```text
plugins/ErpDoctor.Plugin.Postgres/bin/Release/net8.0/ErpDoctor.Plugin.Postgres.dll
```

Its output directory also contains Npgsql and the runtime dependencies required by the plugin. `PluginHost` resolves those dependencies from the plugin directory.

## Connection string

ERP Doctor JSON stores only the **name of an environment variable**, never the PostgreSQL connection string itself.

PowerShell example:

```powershell
$env:ERP_DOCTOR_POSTGRES="Host=localhost;Port=5432;Database=erp;Username=erp_doctor;Password=..."
```

Configuration:

```json
{
  "plugins": {
    "assemblies": [
      "plugins/ErpDoctor.Plugin.Postgres/bin/Release/net8.0/ErpDoctor.Plugin.Postgres.dll"
    ],
    "settings": {
      "postgres": {
        "connectionStringEnvironmentVariable": "ERP_DOCTOR_POSTGRES",
        "connectionTimeoutSeconds": 5,
        "commandTimeoutSeconds": 10,
        "databaseSizeWarningGb": 20,
        "longRunningWarningSeconds": 30,
        "blockingWarningSeconds": 10
      }
    }
  }
}
```

See [`samples/postgres-plugin.example.json`](../samples/postgres-plugin.example.json).

## Run

Confirm the plugin can be discovered without executing any database check:

```bash
erp-doctor plugins --config postgres-plugin.example.json
```

Run only plugin checks:

```bash
erp-doctor plugin --config postgres-plugin.example.json
```

Or include PostgreSQL evidence in the normal whole-system run:

```bash
erp-doctor check --config postgres-plugin.example.json
```

Plugin checks are also included in `report` and `bundle`.

## Evidence boundary

The plugin deliberately does **not** include SQL statement text from `pg_stat_activity`.

Long-running evidence is limited to bounded metadata such as:

```text
longRunningCount=2
longestSeconds=75.2
pids=101,202
```

Blocking evidence is limited to blocked PID, blocking PID(s), age, and wait-event metadata:

```text
pid=301; blockers=401,402; age=18.4s; wait=Lock:transactionid
```

The connection string and password are never added to summaries or evidence.

## Read-only behavior

The current plugin executes read-only inspection queries only. It does not call:

- `pg_terminate_backend`
- `pg_cancel_backend`
- `VACUUM`
- `REINDEX`
- DDL
- DML against application tables

It never automatically ends a blocking or long-running session. The output identifies backend PIDs so an operator can investigate before taking an explicit action with their normal PostgreSQL tooling.

## Permissions

Connectivity and database size usually work with ordinary database access.

`pg_stat_activity` exposes different amounts of information depending on PostgreSQL permissions and server configuration. ERP Doctor intentionally avoids selecting query text, but visibility of other-session details can still depend on the connected role. If PostgreSQL rejects a diagnostic query, the PluginHost reports the check as an Error instead of escalating privileges.

Use a dedicated least-privilege monitoring account when appropriate for the environment.

## Bounded configuration

To prevent accidental unbounded checks, values are clamped by the plugin:

- connection timeout: 1–30 seconds
- command timeout: 1–60 seconds
- database-size warning: 0.1–100,000 GB
- long-running threshold: 1–86,400 seconds
- blocking threshold: 1–86,400 seconds
- long-running/blocking result sets: max 50 rows per query

## Why this is a plugin instead of Core

PostgreSQL brings its own provider dependency (`Npgsql`) and database-specific behavior. Keeping it in `plugins/` means:

- `ErpDoctor.Core` remains provider-neutral,
- users who only diagnose SQL Server do not inherit Npgsql at runtime,
- future PostgreSQL releases can evolve separately,
- the same SDK pattern can be reused by Redis, Docker, RabbitMQ, Nginx, or company-specific ERP diagnostics.
