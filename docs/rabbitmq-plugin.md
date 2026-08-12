# RabbitMQ diagnostics provider

`ErpDoctor.Plugin.RabbitMq` is a read-only Plugin SDK provider for RabbitMQ clusters that expose the Management HTTP API.

It contributes three checks:

```text
plugin.rabbitmq.overview
plugin.rabbitmq.nodes
plugin.rabbitmq.queues
```

## What it reads

The provider sends HTTP **GET** requests only:

```text
GET /api/overview
GET /api/nodes
GET /api/queues?page=1&page_size=<maxQueues>&pagination=true
```

When `virtualHost` is configured, the queue request is scoped to:

```text
GET /api/queues/<encoded-vhost>?page=1&page_size=<maxQueues>&pagination=true
```

`maxQueues` is hard-capped at 500 so diagnostics cannot accidentally request an unbounded queue list.

The provider never calls definitions export, message retrieval, publish, purge, delete, create/update, connection-close, consumer-cancel, requeue, or topology mutation endpoints.

## Prerequisites

- RabbitMQ Management plugin/API must be enabled and reachable from the machine running ERP Doctor.
- The configured account must be allowed to read the required management endpoints.
- Use HTTPS for remote/untrusted networks.
- Passwords must be supplied through an environment variable, not JSON.

## Authentication

PowerShell:

```powershell
$env:ERP_DOCTOR_RABBITMQ_PASSWORD="your-secret"
```

Linux/macOS shell:

```bash
export ERP_DOCTOR_RABBITMQ_PASSWORD='your-secret'
```

Configuration stores only the environment-variable name:

```json
{
  "plugins": {
    "assemblies": [
      "plugins/ErpDoctor.Plugin.RabbitMq/bin/Release/net8.0/ErpDoctor.Plugin.RabbitMq.dll"
    ],
    "settings": {
      "rabbitmq": {
        "baseUrl": "https://rabbit.example.internal:15672",
        "username": "erp-doctor",
        "passwordEnvironmentVariable": "ERP_DOCTOR_RABBITMQ_PASSWORD"
      }
    }
  }
}
```

The Basic Authorization header is constructed in memory for the outbound request. ERP Doctor does not place the password in the URL, config file, report evidence, or error output.

The provider rejects base URLs that:

- are not absolute HTTP/HTTPS URLs,
- embed credentials in URL user-info.

TLS certificate validation uses the operating system defaults. There is no insecure certificate-bypass switch in this provider.

## Overview check

`plugin.rabbitmq.overview` reads high-level management API state and emits a bounded whitelist such as:

- RabbitMQ version,
- management version,
- cluster name,
- connection/channel/exchange/queue/consumer counts,
- total/ready/unacknowledged message counts.

It does not serialize arbitrary overview fields.

## Node health check

`plugin.rabbitmq.nodes` inspects broker-native state for each node:

- running/down state,
- memory alarms,
- disk free alarms,
- network partitions.

Any down node, memory alarm, disk alarm, or reported partition is `Critical` because these are direct RabbitMQ health signals rather than ERP Doctor guesses.

Evidence is limited to aggregate counts plus a bounded list of affected node names/reasons. Config/log file paths and unrelated node metadata are not exported.

## Queue backlog check

`plugin.rabbitmq.queues` inspects only:

- queue name,
- virtual host,
- `messages_ready`,
- `messages_unacknowledged`,
- consumer count.

Default thresholds:

```json
{
  "readyMessagesWarning": 1000,
  "readyMessagesCritical": 10000,
  "unackedMessagesWarning": 500,
  "unackedMessagesCritical": 5000,
  "warnOnNoConsumersWithReadyMessages": false,
  "maxQueueEvidence": 10
}
```

Critical thresholds are clamped so they cannot fall below warning thresholds.

`warnOnNoConsumersWithReadyMessages` is disabled by default because queues can intentionally have no consumers for delayed/offline workflows. Enable it only when that condition is unexpected in your environment.

Queue evidence is sorted by severity/backlog and capped by `maxQueueEvidence`. Queue names and virtual-host names are operational identifiers and can therefore appear in reports/support bundles.

## Pagination and scan scope

RabbitMQ queue listings can be large. ERP Doctor requests only page 1 and limits `page_size` with `maxQueues` (1–500).

If RabbitMQ reports more queues than were inspected, the result includes:

```text
scanTruncated=true
```

and recommends either:

- setting `virtualHost` to narrow the scan, or
- increasing `maxQueues` up to 500 when that is safe for the broker.

ERP Doctor does not silently fetch unlimited pages.

Example vhost scope:

```json
{
  "virtualHost": "/erp",
  "maxQueues": 100
}
```

## Response safety

Every management HTTP request has:

- bounded timeout (1–60 seconds),
- maximum response body of 2,000,000 bytes,
- system TLS certificate validation,
- no error-body serialization.

401/403/404 and other HTTP errors are converted into generic diagnostics. Response bodies from failed requests are not copied into evidence because reverse proxies and broker errors may contain sensitive operational context.

## What the provider will not do

The provider intentionally contains no mutation path for:

```text
publish messages
get/requeue messages
purge queues
delete/create queues or exchanges
change bindings/users/permissions/policies
close connections
cancel consumers
export/import definitions
```

If diagnostics find a backlog or alarm, ERP Doctor recommends investigation; it does not attempt an automatic fix.

## Run

Build:

```bash
dotnet build ErpDoctor.sln --configuration Release
```

Discover the plugin without making RabbitMQ requests:

```bash
erp-doctor plugins --config samples/rabbitmq-plugin.example.json
```

Run contributed checks:

```bash
erp-doctor plugin --config samples/rabbitmq-plugin.example.json
```

For broad historical monitoring and alerting, use RabbitMQ's normal monitoring/metrics ecosystem; this provider is designed for bounded, incident-oriented ERP Doctor diagnostics.
