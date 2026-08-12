# Nginx diagnostics plugin

`ErpDoctor.Plugin.Nginx` is a read-only Plugin SDK provider for Linux hosts running Nginx.

Starting in ERP Doctor v0.17, generic Linux CPU/load/process pressure belongs to the built-in **System Doctor**. This provider stays focused on Nginx itself so a whole-system run does not report the same host resource signals twice.

## Checks

The provider contributes two checks:

```text
plugin.nginx.version
plugin.nginx.config
```

### Nginx version

The provider executes:

```text
nginx -v
```

It parses the version and emits only the bounded version value. Raw command output is not copied into failure diagnostics.

### Nginx configuration validation

The provider executes:

```text
nginx -t -q
```

When `configPath` is configured, it executes:

```text
nginx -t -q -c <configured-path>
```

Nginx documents `-t` as testing configuration syntax and attempting to open referenced files, while `-q` suppresses non-error messages during configuration testing. A failed test is therefore `Critical`, but ERP Doctor intentionally suppresses raw Nginx stderr in reports because configuration errors can contain environment-specific paths or values.

ERP Doctor does not execute:

```text
nginx -T
nginx -s reload
nginx -s stop
nginx -s quit
nginx -s reopen
```

It never dumps the complete configuration and never changes Nginx process state.

## Configuration

```json
{
  "plugins": {
    "assemblies": [
      "plugins/ErpDoctor.Plugin.Nginx/bin/Release/net8.0/ErpDoctor.Plugin.Nginx.dll"
    ],
    "settings": {
      "nginx": {
        "nginxExecutable": "nginx",
        "configPath": "/etc/nginx/nginx.conf",
        "commandTimeoutSeconds": 10
      }
    }
  }
}
```

See [`samples/nginx-plugin.example.json`](../samples/nginx-plugin.example.json).

`configPath` is optional. When omitted, Nginx validates its normal/default configuration path. Command timeout is bounded to 1–60 seconds.

For host CPU/load/process diagnostics use:

```bash
erp-doctor system --config erp-doctor.json
```

See [`system-pressure.md`](system-pressure.md).

## Build and run

Build the solution:

```bash
dotnet build ErpDoctor.sln --configuration Release
```

Discover the provider without executing its checks:

```bash
erp-doctor plugins --config samples/nginx-plugin.example.json
```

Run configured plugin checks:

```bash
erp-doctor plugin --config samples/nginx-plugin.example.json
```

Or include the provider in the whole-system run:

```bash
erp-doctor check --config samples/nginx-plugin.example.json
```

On non-Linux hosts both contributed checks are `Skipped`. This lets the assembly remain build/testable cross-platform without pretending that Windows is a supported Nginx runtime target.

## Command safety

Nginx processes are started with `ProcessStartInfo.ArgumentList` and `UseShellExecute=false`; the provider does not construct or invoke a shell command.

Command execution is bounded by a configurable timeout and a 100,000-character stdout/stderr capture ceiling. A timeout kills the child process tree. Raw command output is never copied into failure evidence.

## Permission behavior

`nginx -t` may fail because the configured process user cannot read a referenced file, open a configured log/file, or otherwise validate the configuration. ERP Doctor reports the non-zero exit as a diagnostic; it does not attempt sudo, privilege escalation, chmod/chown, service restart, or automatic configuration repair.

Run ERP Doctor under an account with the inspection permissions appropriate for the target environment.

## Why this is a plugin

Nginx diagnostics are useful for reverse-proxy deployments but unnecessary for Windows/IIS installations. Keeping them in `plugins/` preserves a provider-neutral Core while generic host health remains in built-in System Doctor.
