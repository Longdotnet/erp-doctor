# Redis diagnostics provider

`ErpDoctor.Plugin.Redis` is a read-only Plugin SDK provider for Redis instances reachable through the official `redis-cli` command-line client.

It contributes five checks:

```text
plugin.redis.connectivity
plugin.redis.server
plugin.redis.memory
plugin.redis.persistence
plugin.redis.replication
```

## What it executes

The provider uses fixed argument lists only and never invokes a shell.

Commands are limited to:

```text
PING
INFO server
INFO memory
INFO persistence
INFO replication
```

It does **not** run `KEYS`, `SCAN`, `GET`, `MGET`, `DUMP`, `CONFIG`, `MONITOR`, `CLIENT LIST`, `SLOWLOG`, or commands that inspect application keys/values.

## Prerequisites

- `redis-cli` must be installed on the machine running ERP Doctor, or `redisCliExecutable` must point to the executable.
- The target host/port must be reachable.
- If authentication is enabled, configure a password environment-variable name rather than a plaintext password.
- If Redis ACLs restrict `INFO`, the diagnostic account must be granted the required read-only command permission. ERP Doctor never attempts privilege escalation.

## Authentication

Do not place the Redis password in the JSON config.

PowerShell:

```powershell
$env:ERP_DOCTOR_REDIS_PASSWORD="your-secret"
```

Linux/macOS shell:

```bash
export ERP_DOCTOR_REDIS_PASSWORD='your-secret'
```

Then reference only the environment-variable name:

```json
{
  "plugins": {
    "assemblies": [
      "plugins/ErpDoctor.Plugin.Redis/bin/Release/net8.0/ErpDoctor.Plugin.Redis.dll"
    ],
    "settings": {
      "redis": {
        "host": "127.0.0.1",
        "port": 6379,
        "passwordEnvironmentVariable": "ERP_DOCTOR_REDIS_PASSWORD"
      }
    }
  }
}
```

The plugin passes the password to the child `redis-cli` process through `REDISCLI_AUTH`. It never adds the password to process arguments or diagnostic evidence. If no password environment variable is configured, the child process explicitly removes any ambient `REDISCLI_AUTH` value before starting.

For ACL users, configure both the username and password environment-variable name:

```json
{
  "username": "erp-doctor",
  "passwordEnvironmentVariable": "ERP_DOCTOR_REDIS_PASSWORD"
}
```

If your Redis instance does not require authentication, remove `passwordEnvironmentVariable` from the plugin settings.

## TLS

Enable TLS with:

```json
{
  "tls": true,
  "caCertificatePath": "/etc/ssl/certs/redis-ca.pem"
}
```

`caCertificatePath` is optional when the system trust store is sufficient.

## Thresholds

Example:

```json
{
  "commandTimeoutSeconds": 10,
  "memoryWarningPercent": 80,
  "memoryCriticalPercent": 90,
  "replicaLagWarningSeconds": 10,
  "replicaLagCriticalSeconds": 30
}
```

The critical threshold is automatically clamped so it cannot be lower than the corresponding warning threshold.

### Memory

When `maxmemory` is configured, ERP Doctor compares `used_memory` with that limit.

- below warning threshold: Healthy
- at/above warning threshold: Warning
- at/above critical threshold: Critical

When `maxmemory` is `0`/unlimited, the result is informational because Redis does not expose a provider-level memory ceiling to compare against. Host memory should then be evaluated separately.

### Persistence

Persistence is Critical when the last RDB background save or enabled AOF background rewrite reports `err`. A dataset currently loading is Warning. ERP Doctor does not trigger saves, rewrites, or repairs.

### Replication

For a primary node, the provider reports role and connected replica count without exposing per-replica address records.

For a replica:

- primary link down: Critical
- synchronization in progress: Warning
- last primary I/O age above warning/critical thresholds: Warning/Critical

The provider never changes replication topology.

## Evidence boundary

The Redis provider deliberately emits only bounded operational metadata such as:

- version and mode,
- uptime,
- memory totals/ratios,
- persistence status flags,
- replication role/link/lag,
- target host/port/TLS state on CLI failures.

It does not emit Redis passwords, raw `redis-cli` stderr, key names, values, `run_id`, config-file paths from INFO, executable paths from INFO, or individual replica address records.

## Run

Build:

```bash
dotnet build ErpDoctor.sln --configuration Release
```

Discover without executing Redis commands:

```bash
erp-doctor plugins --config samples/redis-plugin.example.json
```

Run only contributed provider checks:

```bash
erp-doctor plugin --config samples/redis-plugin.example.json
```

If an INFO command is denied by ACL policy, ERP Doctor reports a normal diagnostic Error and does not attempt to grant permissions or switch credentials.
