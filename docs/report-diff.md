# Diagnostic report diff

ERP Doctor v0.20 can compare two existing `DiagnosticReport` JSON documents without connecting to the ERP environment again.

This is intended for before/after deployment checks, incident comparisons, machine-to-machine comparisons, and CI regression gates.

## Usage

```bash
erp-doctor report-diff \
  --left before.json \
  --right after.json
```

Machine-readable output:

```bash
erp-doctor report-diff \
  --left before.json \
  --right after.json \
  --json -
```

Or write an indented diff document:

```bash
erp-doctor report-diff \
  --left before.json \
  --right after.json \
  --json report-diff.json
```

`report-diff` does not require `erp-doctor.json`. It reads only the two report files supplied with `--left` and `--right`.

## Exit codes

| Exit | Meaning |
| --- | --- |
| `0` | Comparison succeeded and no regression was found |
| `1` | Comparison succeeded and at least one regression was found |
| `2` | Invalid/missing input, unsupported report schema, duplicate check ID, or unreadable/invalid JSON |
| `130` | Comparison was cancelled |

This makes the command usable as a post-deployment CI gate:

```bash
erp-doctor report-diff --left baseline.json --right candidate.json
```

A newly degraded diagnostic fails the step with exit `1`, while a clean or improved candidate exits `0`.

## What is compared

Checks are matched case-insensitively by stable `checkId`.

The diff compares diagnostic **status and coverage**, not volatile evidence values.

Change kinds are:

```text
Unchanged
Improved
Regressed
Changed
Added
Removed
```

The output also includes:

- left/right report metadata,
- left/right health score,
- health-score delta (`right - left`),
- per-kind counts,
- `regressionCount`,
- `hasRegression`,
- each check's before/after status and summary.

## Regression rules

Normal status severity is interpreted as:

```text
Healthy < Info < Warning < Critical < Error
```

A comparison is considered a regression when any of these happens:

- a check moves to a worse normal status,
- an existing check disappears from the right report,
- an existing non-skipped check becomes `Skipped`,
- a newly added check starts as `Warning`, `Critical`, or `Error`.

The following do **not** fail the regression gate:

- a check improves to a better normal status,
- a previously `Skipped` check becomes observable again,
- a newly added `Healthy` or `Info` check,
- unchanged status.

A removed check is treated as regression because diagnostic coverage was lost even if that check was previously healthy.

A transition to `Skipped` is also treated as regression because observability was lost. A transition from `Skipped` is reported as changed/restored coverage rather than claiming the health itself improved.

## Why evidence is not diffed

`DiagnosticResult.Evidence` and `Duration` are deliberately excluded from v0.20 regression classification.

Values such as latency, free memory, process working set, row counts, timestamps, server identifiers, and provider metadata may legitimately change between runs. Treating every value change as a regression would create noisy and unstable deployment gates.

The source reports remain available when an operator needs the detailed evidence behind a changed status.

This also avoids creating a second policy system inside the diff engine. Thresholds remain owned by the diagnostic checks that produced the original statuses.

## Schema rules

Both inputs must use the currently supported `DiagnosticReport` schema:

```json
{
  "schemaVersion": "1.0"
}
```

Unsupported report schema versions fail with exit `2` rather than being compared approximately.

Each report must also contain unique `checkId` values. Duplicate IDs are rejected case-insensitively because an ambiguous baseline/candidate match would make the result unreliable.

The report-diff output has its own versioned schema, currently `1.0`.

Example shape:

```json
{
  "schemaVersion": "1.0",
  "left": {
    "reportSchemaVersion": "1.0",
    "healthScore": 100,
    "totalChecks": 2
  },
  "right": {
    "reportSchemaVersion": "1.0",
    "healthScore": 80,
    "totalChecks": 2
  },
  "healthScoreDelta": -20,
  "regressionCount": 1,
  "hasRegression": true,
  "changes": [
    {
      "checkId": "system.cpu",
      "kind": "regressed",
      "beforeStatus": "healthy",
      "afterStatus": "warning",
      "isRegression": true
    }
  ]
}
```

Additional snapshot/change properties may be present; consumers should rely on the documented versioned schema contract rather than console formatting.

## Deployment workflow example

Before deployment:

```bash
erp-doctor check --config erp-doctor.json --json before.json
```

Deploy the application, then capture the candidate report:

```bash
erp-doctor check --config erp-doctor.json --json after.json
```

Compare:

```bash
erp-doctor report-diff --left before.json --right after.json
```

For machine-readable CI:

```bash
set +e
DIFF_JSON="$(erp-doctor report-diff --left before.json --right after.json --json -)"
DIFF_EXIT=$?
set -e

echo "$DIFF_JSON" | jq '.healthScoreDelta, .regressionCount, .changes'
exit "$DIFF_EXIT"
```

Remember that `1` means a **valid comparison that found a regression**, not a transport or parsing failure.

## Safety and privacy

`report-diff` is offline and read-only:

- it does not load ERP configuration,
- it does not connect to SQL Server, HTTP endpoints, IIS, Docker, brokers, or plugins,
- it does not execute diagnostics,
- it does not modify either input report,
- it does not mutate the environment,
- it does not expose a repair action.

The console/diff JSON carries check IDs, names, statuses, and summaries from the source reports. Operational identifiers may therefore still be present. Review report/diff artifacts before sharing them outside the organization.
