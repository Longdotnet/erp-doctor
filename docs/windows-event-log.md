# Windows Event Log diagnostics

ERP Doctor v0.7 can inspect recent Windows Event Log entries and place the most relevant errors next to IIS, HTTP, SQL, disk, and runtime evidence from the same diagnostic run.

```bash
erp-doctor eventlog --config erp-doctor.json
```

Configured Event Log checks are also included by `erp-doctor check`, `report`, and `bundle`.

## Configuration

```json
{
  "windowsEventLog": {
    "queries": [
      {
        "name": "ERP application errors",
        "logName": "Application",
        "lookbackMinutes": 60,
        "maxEvents": 20,
        "includeWarnings": false,
        "providers": [
          ".NET Runtime",
          "Application Error",
          "IIS AspNetCore Module V2"
        ]
      }
    ]
  }
}
```

`providers` is optional. When empty, ERP Doctor accepts every provider returned by the configured channel and severity query. Provider matching is exact and case-insensitive.

## Severity

ERP Doctor queries the newest matching entries first.

- Windows level `1` (Critical) makes the check `Critical`.
- Windows level `2` (Error) makes the check `Warning` unless a Critical entry is also present.
- `includeWarnings=true` also includes Windows level `3` (Warning), which produces `Warning` status when no Error/Critical event is present.
- No matching recent event produces `Healthy`.

An Error event is intentionally not treated as an automatic ERP outage. The event should be correlated with HTTP, IIS, SQL, disk, and other evidence before escalating the diagnosis.

## Limits

`lookbackMinutes` is clamped to 1-10080 minutes (up to seven days).

`maxEvents` is clamped to 1-100 entries per configured query. This prevents a noisy Windows server from producing an unbounded diagnostic report.

## Message handling

ERP Doctor reads the rendered Windows Event XML. When a rendered message is unavailable it falls back to the event's `EventData` fields.

Before event text is placed into diagnostic evidence, common secret-like fragments such as passwords, tokens, API keys, authorization values, and Bearer tokens are redacted. Long event text is truncated to keep reports practical.

This is best-effort sanitization, not full anonymization. Event messages can still contain machine names, file paths, database names, URLs, business identifiers, and application-specific data. Review reports/support bundles before sending them outside your organization.

## Implementation

The collector uses the native read-only Windows Event Log API from `wevtapi.dll`:

- `EvtQuery`
- `EvtNext`
- `EvtRender`
- `EvtClose`

No additional NuGet dependency is required.

## Permissions and platform

Windows Event Log collection runs only on Windows. The current account must have permission to read the configured event channel. A missing channel or access failure is reported as an `Error` diagnostic rather than silently ignored.

## Safety

ERP Doctor never:

- clears an Event Log,
- deletes entries,
- changes channel retention/settings,
- enables/disables providers,
- writes synthetic events.

The feature only queries, renders, summarizes, and closes event handles.
