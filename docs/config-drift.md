# Configuration drift

ERP Doctor v0.5 can compare two local JSON/appsettings files and show the settings that differ without printing secret values.

```bash
erp-doctor config-diff \
  --left appsettings.Development.json \
  --right appsettings.Production.json
```

This is designed for a common enterprise support question: **"it works on DEV, so what is different on the customer/server environment?"**

## Output

Nested JSON is compared by configuration path:

```text
ERP Doctor - Configuration Drift
────────────────────────────────────────────────────────────────────────
Left  : D:\app\appsettings.Development.json
Right : D:\app\appsettings.Production.json
Drift : 3 difference(s)

~ Api:BaseUrl (different)
  left  : https://dev.example.test
  right : https://prod.example.test

- FeatureFlags:NewCheckout (only on left)
  left  : true
  right : [MISSING]

~ ConnectionStrings:ERP (different)
  left  : [SET]
  right : [SET]
  note  : sensitive values are redacted; ERP Doctor does not hash or print them.
```

`config-diff` returns exit code `1` when drift exists, which makes it usable as a deployment/preflight gate. Exit code `0` means the compared configuration is equivalent for the paths being checked. Invalid arguments/files/JSON return `2`.

## Secret handling

ERP Doctor compares sensitive values in memory so it can still tell that they differ, but it never prints or hashes the raw value.

Paths containing common credential concepts are treated as sensitive, including:

- `ConnectionStrings`
- password / pwd
- token
- secret / client secret
- API key / access key / private key
- authorization

Sensitive values are displayed as:

```text
[SET]
```

Missing values are displayed as:

```text
[MISSING]
```

Strings on non-sensitive paths are also sanitized for common inline credential fragments such as `token=...`, `password=...`, `apiKey=...`, and Bearer tokens before being printed.

ERP Doctor intentionally does **not** hash secrets. Hashes of low-entropy credentials can create an unnecessary offline guessing surface and are not needed to answer whether two values differ.

## Ignore noisy sections

Ignore one or more path prefixes with `--ignore`:

```bash
erp-doctor config-diff \
  --left appsettings.Development.json \
  --right appsettings.Production.json \
  --ignore "Logging,Serilog"
```

A prefix ignores the entire subtree. Matching is case-insensitive to align with normal .NET configuration behavior.

## Comparison semantics

- JSON object property names are compared case-insensitively.
- Scalar values are compared exactly.
- Missing values are reported explicitly.
- Type changes are reported once at the parent path; noisy descendant missing entries are suppressed.
- Arrays are order-sensitive and are compared by index.
- JSON comments and trailing commas are accepted.
- Equivalent values are omitted from the report.

## Safety boundary

`config-diff` only reads the two local files. It does not load the values into SQL Server, call application endpoints, update appsettings, or change either file.

Non-secret values such as URLs, environment names, company codes, feature flags, and host names may be printed because they are the evidence needed to diagnose configuration drift. Review terminal output before sharing it outside your organization.
