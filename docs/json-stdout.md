# Machine-readable JSON stdout

ERP Doctor v0.18 can write the stable diagnostic-report schema directly to standard output:

```bash
erp-doctor check --config erp-doctor.json --json -
```

`-` means stdout. In this mode ERP Doctor writes **one compact JSON document** to stdout and suppresses the normal human console report.

This is intended for CI pipelines, scripts, orchestration, local agents, and thin integration/MCP wrappers that should consume ERP Doctor evidence without parsing presentation text.

## Supported diagnostic commands

`--json -` works with commands that produce the normal `DiagnosticReport` envelope:

```text
check
report
system
sql
http
network
iis
eventlog
plugin
```

Examples:

```bash
erp-doctor system --json -
erp-doctor network --config erp-doctor.json --json -
erp-doctor plugin --config postgres-plugin.json --json -
```

The following specialized commands currently use different output models and therefore reject `--json -` with usage exit code `2`:

```text
growth
config-diff
plugins
bundle
```

That rejection is intentional rather than silently mixing incompatible shapes into one integration surface.

## Contract

The stdout document uses the same versioned report contract as file JSON output. Current schema version:

```text
1.0
```

Minimal example:

```json
{"schemaVersion":"1.0","generatedAtUtc":"2026-08-12T10:00:00+00:00","overallStatus":"healthy","healthScore":100,"summary":{"total":1,"healthy":1,"info":0,"warning":0,"critical":0,"skipped":0,"error":0},"results":[],"diagnoses":[]}
```

Consumers should always inspect `schemaVersion` before assuming field semantics. See [`report-schema.md`](report-schema.md).

The stdout representation is compact to reduce transport/log overhead. Writing to a file remains indented:

```bash
erp-doctor check --json report.json
```

Both forms use the same camelCase property names and string enum values.

## Stdout/stderr boundary

In `--json -` mode:

- stdout contains the report JSON only,
- the human console report is suppressed,
- configuration/usage/runtime messages that occur before a report can be produced go to stderr,
- no success/path banner is appended after the JSON.

This boundary lets a consumer parse stdout directly:

```bash
erp-doctor system --json - | jq '.overallStatus, .healthScore'
```

PowerShell:

```powershell
$report = erp-doctor system --json - | ConvertFrom-Json
$report.schemaVersion
$report.results
```

## Output conflict guard

`--json -` cannot be combined with `--html` or `--bundle`:

```bash
erp-doctor check --json - --html report.html
```

The invocation returns usage exit code `2`, writes the explanation to stderr, writes nothing to stdout, and creates no requested artifact.

This prevents a machine consumer from accidentally receiving a JSON document while the command also performs unrelated output side effects.

## Exit codes

JSON stdout mode preserves ERP Doctor's normal diagnostic exit behavior:

```text
0   Diagnostic run completed with no Critical/Error result
1   Diagnostic run completed and at least one result is Critical/Error
2   Invalid usage or configuration before the diagnostic run
130 Diagnostic run cancelled
```

A nonzero diagnostic exit does **not** imply stdout is invalid. Exit code `1` can accompany a fully valid JSON report describing a Critical condition. Consumers should parse the report and use `overallStatus`, `summary`, and individual results rather than treating every nonzero exit as a transport failure.

## Integration pattern

A thin integration should treat ERP Doctor as the diagnostic authority and avoid re-querying production systems independently:

```text
ERP Doctor checks
      |
      v
versioned DiagnosticReport JSON
      |
      +--> CI policy
      +--> support automation
      +--> local agent
      +--> MCP wrapper
      +--> dashboard/importer
```

Recommended wrapper behavior:

1. invoke an explicitly chosen ERP Doctor command/config,
2. capture stdout and stderr separately,
3. parse stdout as JSON when exit code is `0` or `1`,
4. verify `schemaVersion`,
5. expose/report the existing evidence and diagnoses,
6. do not reinterpret a suggestion as permission to mutate production state.

## Safety

JSON stdout adds no listener, HTTP server, daemon, network exposure, or credential store. It is only a serialization/transport mode over the existing read-only diagnostic pipeline.

The mode does not weaken provider boundaries or authorize repair actions. Third-party plugins remain executable code and should only be loaded when trusted.
