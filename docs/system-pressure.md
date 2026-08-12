# System pressure diagnostics

ERP Doctor v0.17 expands the built-in System Doctor with bounded CPU, Linux load-average, and process working-set evidence.

Run the system category with:

```bash
erp-doctor system --config erp-doctor.json
```

The checks also participate in `check`, `report`, and `bundle` runs.

## System CPU

Check ID:

```text
system.cpu
```

ERP Doctor samples aggregate host CPU counters over a short configurable interval.

- Windows uses `GetSystemTimes`.
- Linux reads the aggregate `cpu` row from `/proc/stat` twice.
- No Performance Counter package, shell command, WMI query, `top`, or `ps` command is required.

Configuration:

```json
{
  "system": {
    "cpuWarningPercent": 80,
    "cpuCriticalPercent": 95,
    "cpuSampleMilliseconds": 250
  }
}
```

The sample interval is clamped to 100–2000 ms. CPU thresholds are bounded to 1–100%, and the effective critical threshold cannot fall below the warning threshold.

Evidence is limited to:

- utilization percentage,
- actual sample duration setting,
- logical processor count.

A short CPU sample is evidence, not proof of sustained pressure. ERP Doctor does not restart services based on one sample.

## Linux load average

Check ID:

```text
system.load
```

On Linux, ERP Doctor reads `/proc/loadavg` and records the 1-, 5-, and 15-minute load averages plus 1-minute load normalized by logical processor count.

Configuration:

```json
{
  "system": {
    "loadPerCpuWarning": 1.0,
    "loadPerCpuCritical": 2.0
  }
}
```

Load average is not the same as CPU utilization: runnable and uninterruptible tasks can increase Linux load. Compare the 1/5/15-minute values before deciding whether the host has sustained pressure.

On non-Linux hosts this check is `Skipped`; Windows still receives the cross-platform `system.cpu` and `system.processes` checks.

## Top process working sets

Check ID:

```text
system.processes
```

ERP Doctor takes a bounded snapshot of processes and sorts them by working-set memory. The check is informational rather than a failure threshold because a large working set is not automatically unhealthy.

Configuration:

```json
{
  "system": {
    "topProcessesLimit": 5
  }
}
```

The limit is clamped to 1–20.

For each selected process ERP Doctor retains only:

```text
PID : process name : working-set MB
```

It deliberately does **not** inspect or serialize:

- command line arguments,
- environment variables,
- process memory content,
- loaded secrets/tokens,
- open file contents,
- network payloads.

Process names are bounded and control/separator characters are removed. Processes that exit or deny metadata access while the snapshot is being collected are skipped rather than causing the whole run to fail.

## HTTP correlation

When `system.cpu` is Warning/Critical while an HTTP endpoint is slow, the diagnosis engine can emit:

```text
Host CPU pressure may be degrading API response time
```

This is correlation, not root-cause proof. The suggestions explicitly ask for repeated/sustained evidence before service restarts or other remediation.

The process snapshot is memory-oriented, not a per-process CPU profiler. Use normal OS tooling to confirm which process is consuming CPU if pressure persists.

## Nginx provider boundary

Before v0.17 the Nginx provider also contributed a generic Linux runtime/load check. v0.17 moves generic host pressure into built-in System Doctor so Linux resource evidence is available even when Nginx is not installed and is not duplicated when the Nginx plugin is loaded.

The Nginx provider now contributes only:

```text
plugin.nginx.version
plugin.nginx.config
```

## Safety

System pressure checks are inspection-only. They do not:

- change process priority or affinity,
- suspend/terminate processes,
- restart services,
- write `/proc` or system configuration,
- run shell process-enumeration commands,
- capture command lines or environment variables,
- change CPU/memory limits.

Use the measurements as bounded evidence and correlate them with application, database, network, and platform diagnostics before remediation.
