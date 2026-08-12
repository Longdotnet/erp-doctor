# Sanitized support bundle

ERP Doctor v0.3 can package a diagnostic run into one ZIP file for support handoff.

```bash
erp-doctor bundle --config erp-doctor.json
```

The default output is `erp-doctor-support.zip`. An explicit path can be supplied with `--bundle`.

## Contents

Every bundle contains exactly three generated files:

- `report.json` — the stable diagnostic report schema after sanitization.
- `report.html` — the standalone HTML report rendered from the same sanitized report.
- `manifest.json` — bundle schema/version metadata and the entry list.

The source configuration file is never copied into the bundle.

## Sanitization

Sanitization happens before JSON serialization and before HTML rendering. ERP Doctor redacts evidence whose keys look credential-related, including password, token, secret, API key, authorization, and connection-string fields. It also replaces common inline `key=value` or `key:value` secret-like fragments inside summaries, suggestions, and diagnosis text.

The replacement value is:

```text
[REDACTED]
```

## Security boundary

A support bundle is safer to share than raw logs or configuration, but it is not an anonymizer. Host names, database names, table names, URLs, machine information, business identifiers, and other non-secret diagnostic evidence can remain when they are useful for troubleshooting.

Review the generated bundle before sending it outside your organization.

## Safety

`bundle` is still a read-only diagnostic operation. It writes only the requested ZIP output and does not restart IIS, modify SQL Server, delete logs, kill sessions, or change ERP data.
