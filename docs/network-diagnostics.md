# Network diagnostics

ERP Doctor v0.16 adds built-in, cross-platform DNS and TCP diagnostics for dependencies such as ERP APIs, SQL Server, PostgreSQL, Redis, RabbitMQ, reverse proxies, and other internal services.

The checks use .NET networking APIs directly. They do not invoke `ping`, `nslookup`, `telnet`, PowerShell, Bash, or another platform-specific shell command.

## Configuration

Add one or more targets under `network.targets`:

```json
{
  "network": {
    "targets": [
      {
        "name": "ERP API",
        "host": "erp-api.internal",
        "port": 443,
        "timeoutSeconds": 5,
        "latencyWarningMs": 1000,
        "maxResolvedAddresses": 5
      },
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

`host` supports the same `${ENVIRONMENT_VARIABLE}` expansion as other ERP Doctor configuration values.

Each configured target contributes two checks:

```text
network.dns.<target-id>
network.tcp.<target-id>
```

Run only network checks with:

```bash
erp-doctor network --config erp-doctor.json
```

They also participate in `check`, `report`, and `bundle` runs.

## DNS check

The DNS check resolves the configured hostname with a bounded timeout.

Healthy evidence is limited to:

- configured host,
- resolution latency,
- total address count,
- at most `maxResolvedAddresses` resolved IP addresses,
- whether the address list was truncated.

`maxResolvedAddresses` is clamped to 1-20 so a DNS response cannot create an unbounded diagnostic payload.

A timeout or resolver/socket failure is Critical. A successful resolution at or above `latencyWarningMs` is Warning.

## TCP check

The TCP check attempts a real connection to the configured host and port with a bounded timeout.

Ports must be in the range `1-65535`. Invalid configuration returns an Error without attempting a connection.

Healthy evidence is limited to:

- configured host,
- configured port,
- connection latency,
- normalized remote IP address.

IPv4-mapped IPv6 addresses such as `::ffff:127.0.0.1` are normalized to their IPv4 representation so reports remain stable across Windows and Linux networking stacks.

On socket failure ERP Doctor records the socket error code, not the raw exception text.

## HTTP correlation

When a configured HTTP endpoint is Critical and ERP Doctor also sees a Critical TCP check for the same host and port, the diagnosis engine can surface the network/listener layer as the likely failure boundary.

For example:

```text
HTTP https://erp-api.internal:443/health  -> Critical
TCP  erp-api.internal:443                -> Critical
```

This helps distinguish a port/listener/firewall/routing problem from an application that is reachable over TCP but returning an unhealthy HTTP response.

The correlation is evidence-based, not proof. ERP Doctor does not alter firewall rules, routing, DNS, listeners, or application processes.

## Safety and scope

Network Doctor is read-only from ERP Doctor's perspective:

- no firewall or DNS changes,
- no route or VPN changes,
- no service restarts,
- no packet capture,
- no port scanning,
- no ICMP dependency,
- no credential exchange beyond whatever the destination service itself would require after TCP connection (ERP Doctor's TCP check sends no application payload),
- no raw socket exception messages copied into evidence.

Each target is explicitly configured. ERP Doctor does not discover or scan neighboring hosts/ports.

## Common targets

Useful examples include:

```text
SQL Server             1433
PostgreSQL             5432
Redis                  6379
RabbitMQ AMQP          5672
RabbitMQ management    15672 (default HTTP; TLS port is deployment-specific)
HTTP                   80
HTTPS                  443
```

Use the actual port configured in your environment rather than assuming these defaults.
