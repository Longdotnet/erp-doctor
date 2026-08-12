# Diagnostic report schema

ERP Doctor v0.2 uses a stable report envelope for both machine-readable JSON output and the standalone HTML report.

Current schema version: `1.0`.

```json
{
  "schemaVersion": "1.0",
  "generatedAtUtc": "2026-08-12T02:30:00+00:00",
  "overallStatus": "warning",
  "healthScore": 80,
  "summary": {
    "total": 5,
    "healthy": 3,
    "info": 0,
    "warning": 2,
    "critical": 0,
    "skipped": 0,
    "error": 0
  },
  "results": [],
  "diagnoses": []
}
```

## Fields

- `schemaVersion`: version of the exported report contract. Consumers should check this before assuming a shape.
- `generatedAtUtc`: UTC timestamp for the diagnostic run.
- `overallStatus`: highest actionable status across diagnostic results and correlated diagnoses.
- `healthScore`: 0-100 score based on checks that actually ran. Skipped checks are not penalized.
- `summary`: counts by diagnostic status.
- `results`: raw diagnostic results, including evidence, suggestions, and duration.
- `diagnoses`: evidence-backed correlations produced by the diagnosis engine.

## Health score

The score is deliberately simple and explainable:

| Status | Score |
|---|---:|
| Healthy | 100 |
| Info | 90 |
| Warning | 60 |
| Critical | 0 |
| Error | 0 |
| Skipped | excluded |

The final score is the rounded average of checks that ran. The score is a prioritization aid, not an SLA or availability metric.

## Compatibility

Adding optional fields does not require a schema-major change. Renaming/removing fields or changing their meaning requires a new schema version.
