# IIS site and binding diagnostics

ERP Doctor v0.6 extends the `iis` diagnostic category beyond application pools. It can inspect configured IIS sites, their current state, root physical path, and protocol/host/port bindings without changing IIS configuration.

## Configuration

```json
{
  "iis": {
    "appPools": ["ErpApi"],
    "sites": [
      {
        "name": "ERP Site",
        "expectedBindings": [
          "https:*:443:erp.example.com"
        ],
        "checkPhysicalPath": true
      }
    ]
  }
}
```

Run IIS-only diagnostics:

```bash
erp-doctor iis --config erp-doctor.json
```

The same checks are also included by `erp-doctor check`, `report`, and `bundle`.

## What is inspected

For each configured site ERP Doctor reads:

- current IIS site state,
- root physical path,
- HTTP/HTTPS bindings,
- whether the physical path currently exists when `checkPhysicalPath` is enabled,
- whether every configured `expectedBindings` entry exists.

Bindings use the compact IIS format:

```text
protocol:ip:port:host
```

Examples:

```text
http:*:80:
https:*:443:erp.example.com
```

Binding comparisons are case-insensitive and whitespace-trimmed. Extra live bindings are retained as evidence but do not fail the check. Missing expected bindings are critical because they can make the intended host/port unreachable even while the site itself is started.

## Status rules

A configured site is `Healthy` when:

- its state is `Started`,
- its root physical path exists when path checking is enabled,
- every expected binding is present.

A site is `Critical` when any of those conditions fail, or when the configured site does not exist.

An inspection problem such as missing IIS management components or insufficient permission is reported as `Error` rather than guessed as an application failure.

## Implementation and permissions

ERP Doctor loads the IIS `Microsoft.Web.Administration.dll` installed with IIS and reads `ServerManager` through reflection. This keeps the feature dependency-free and avoids requiring a separate NuGet package.

The process still needs Windows permission to inspect IIS configuration. On non-Windows systems this diagnostic is skipped.

## Safety

IIS site diagnostics are read-only. ERP Doctor does not:

- start or stop sites,
- start or stop application pools,
- add/remove/change bindings,
- change physical paths,
- rewrite `applicationHost.config`.

The resulting report may include server-local paths and host names because those are useful deployment evidence. Review exported reports before sharing them outside your organization.
