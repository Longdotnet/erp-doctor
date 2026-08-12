# Docker diagnostics plugin

`ErpDoctor.Plugin.Docker` is a read-only reference provider built on the ERP Doctor Plugin SDK. It uses the installed Docker CLI instead of adding a Docker SDK dependency to Core.

## Requirements

- Docker CLI available on the machine running ERP Doctor.
- The current Docker context must be able to reach the intended Docker Engine.
- The account running ERP Doctor must have permission to inspect that engine.

## Checks

The plugin contributes three checks:

```text
plugin.docker.engine
plugin.docker.info
plugin.docker.containers
```

`engine` reads Docker Engine version/API/OS/architecture metadata.

`info` reads bounded engine summary counts such as total/running/paused/stopped containers, image count, and the number of daemon warnings. The warning messages themselves are deliberately not copied into ERP Doctor evidence.

`containers` inspects container name, state, and health only. It can validate an optional list of expected container names.

## Fixed read-only commands

The current provider executes only these command shapes:

```text
docker version --format json
docker info --format json
docker ps --all --format json
```

Arguments are added with `ProcessStartInfo.ArgumentList`; the plugin does not build a shell command string and does not invoke a shell.

The provider does **not** run commands such as:

```text
docker start
docker stop
docker restart
docker rm
docker kill
docker exec
docker run
docker compose up
docker system prune
```

It never changes Docker state automatically.

## Evidence boundary

Container evidence is intentionally limited to:

```text
name
state
health
```

The plugin does not collect or emit:

- container environment variables,
- labels,
- command/entrypoint,
- mounts,
- secrets,
- raw Docker stderr.

Engine-level evidence is limited to bounded metadata/counts.

Docker CLI stdout/stderr capture is capped at 1,000,000 characters. Commands are timed out and terminated if they exceed the configured limit. Raw stderr is never returned in the diagnostic result.

## Severity model

Container state rules are designed for ERP/server diagnostics without treating every old stopped job as an incident.

`Critical`:

- a configured expected container is missing,
- a configured expected container is not running,
- a container reports health `unhealthy`,
- a container is in `dead`, `restarting`, or `removing` state.

`Warning`:

- a container is paused,
- a stopped/exited container exists **only when** `warnOnStoppedContainers` is enabled.

`Healthy`:

- expected containers exist and are running,
- no unhealthy/severe/paused condition exists,
- stopped containers are ignored when the opt-in warning is disabled.

## Configuration

```json
{
  "plugins": {
    "assemblies": [
      "plugins/ErpDoctor.Plugin.Docker/bin/Release/net8.0/ErpDoctor.Plugin.Docker.dll"
    ],
    "settings": {
      "docker": {
        "dockerExecutable": "docker",
        "commandTimeoutSeconds": 10,
        "warnOnStoppedContainers": false,
        "maxContainerEvidence": 20,
        "expectedContainers": [
          "erp-api",
          "redis"
        ]
      }
    }
  }
}
```

See [`samples/docker-plugin.example.json`](../samples/docker-plugin.example.json).

`dockerExecutable` can also be an explicit local path when `docker` is not on PATH.

Bounded settings:

- `commandTimeoutSeconds`: 1–60 seconds,
- `maxContainerEvidence`: 1–100 rows,
- `expectedContainers`: first 100 distinct names.

## Build and run

Build the solution:

```bash
dotnet build ErpDoctor.sln --configuration Release
```

Confirm the provider can be discovered without executing Docker checks:

```bash
erp-doctor plugins --config samples/docker-plugin.example.json
```

Run only configured plugin checks:

```bash
erp-doctor plugin --config samples/docker-plugin.example.json
```

Or include Docker diagnostics in the whole-system run:

```bash
erp-doctor check --config samples/docker-plugin.example.json
```

## Failure behavior

If the Docker CLI is missing, the engine cannot be reached, a command times out, a command exits non-zero, or JSON cannot be parsed, the relevant plugin check returns `Error` instead of crashing ERP Doctor.

Raw stderr is not included in output because it can contain environment-specific or sensitive data. Operators can reproduce the fixed Docker command manually when deeper troubleshooting is required.

## Why this is a plugin

Docker is useful across ERP deployments but is not required by every installation. Keeping Docker diagnostics in `plugins/` means:

- Core stays provider-neutral,
- Windows/IIS/SQL Server users do not inherit a Docker dependency,
- Docker behavior can evolve independently,
- the same Plugin SDK architecture remains reusable for Nginx, Redis, RabbitMQ, and company-specific infrastructure.
