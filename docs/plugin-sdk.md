# Plugin SDK

ERP Doctor v0.8 can load external diagnostic checks without changing `ErpDoctor.Core` or the CLI.

## Trust boundary

A plugin DLL is executable .NET code. It runs inside the ERP Doctor process with the same operating-system permissions as ERP Doctor itself.

ERP Doctor therefore:

- loads plugins only from DLL paths explicitly listed in configuration,
- never downloads plugin assemblies from URLs,
- rejects incompatible plugin API versions,
- namespaces contributed checks as `plugin.<plugin-id>.<check-id>`,
- converts discovery/load failures into normal `Error` diagnostics,
- suppresses raw plugin exception messages because they can accidentally contain secrets.

Only load plugin assemblies you trust. ERP Doctor's built-in read-only guarantee does **not** automatically make third-party plugin code read-only.

## Projects

`src/ErpDoctor.PluginSdk` contains the public plugin contract and intentionally does not reference `ErpDoctor.Core`.

`src/ErpDoctor.PluginHost` is the ERP Doctor runtime adapter. It loads plugin assemblies, resolves their dependencies, validates metadata, and maps plugin results into `DiagnosticResult`.

`samples/ErpDoctor.SamplePlugin` is a compile-tested example plugin.

## Plugin contract

A plugin implements `IErpDoctorPlugin` and returns one or more `IPluginCheck` instances:

```csharp
using ErpDoctor.PluginSdk;

public sealed class MyPlugin : IErpDoctorPlugin
{
    public string Id => "my-company";
    public string Name => "My Company Diagnostics";
    public string Version => "0.1.0";

    public IReadOnlyList<IPluginCheck> CreateChecks(PluginContext context) =>
        [new MyCheck()];
}
```

A check returns a `PluginDiagnosticResult`:

```csharp
public sealed class MyCheck : IPluginCheck
{
    public string Id => "dependency";
    public string Name => "Dependency availability";
    public string Category => "dependency";

    public Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new PluginDiagnosticResult(
            PluginDiagnosticStatus.Healthy,
            "Dependency is reachable."));
    }
}
```

Plugin IDs and check IDs must be 1-64 characters, start with a letter or digit, and contain only letters, digits, `.`, `_`, or `-`.

The current plugin API version is `1`. A plugin may override `ApiVersion`; the host rejects versions it does not understand instead of attempting to run them.

## Configuration

Plugin assemblies are explicit local paths. Relative paths are resolved from the directory containing the ERP Doctor configuration file.

```json
{
  "plugins": {
    "assemblies": [
      "plugins/ErpDoctor.Plugin.Postgres.dll"
    ],
    "settings": {
      "postgres": {
        "connectionStringEnvironmentVariable": "POSTGRES_DB"
      }
    }
  }
}
```

Assembly path strings support ERP Doctor's `${ENV_VAR}` expansion. Plugin-specific `settings` are passed to the plugin as JSON and are not automatically rewritten or expanded by the host.

That boundary is intentional: the plugin owns its own configuration and secret-loading strategy.

## Commands

List configured plugins without running contributed checks:

```bash
erp-doctor plugins --config erp-doctor.json
```

Run only plugin checks:

```bash
erp-doctor plugin --config erp-doctor.json
```

Plugin checks are also included in:

```bash
erp-doctor check
erp-doctor report
erp-doctor bundle
```

They are not loaded for narrow built-in commands such as `sql`, `iis`, `eventlog`, or `growth`.

## Build and try the sample plugin

Build the solution:

```bash
dotnet build ErpDoctor.sln --configuration Release
```

Then point configuration at the generated sample DLL. For example on Windows from the repository root:

```json
{
  "plugins": {
    "assemblies": [
      "samples/ErpDoctor.SamplePlugin/bin/Release/net8.0/ErpDoctor.Plugin.Sample.dll"
    ],
    "settings": {
      "sample": {
        "requiredEnvironmentVariable": "ERP_SAMPLE_READY"
      }
    }
  }
}
```

Run discovery:

```bash
dotnet run --project src/ErpDoctor.Cli -- plugins --config erp-doctor.json
```

Then run the sample check:

```bash
dotnet run --project src/ErpDoctor.Cli -- plugin --config erp-doctor.json
```

The sample plugin checks whether a configured environment variable exists but deliberately never places the variable value into diagnostic output.

## Dependency loading

Each plugin is loaded with an `AssemblyDependencyResolver`-backed load context. The host always shares its own `ErpDoctor.PluginSdk` assembly with the plugin so interface identity remains stable, while plugin-private dependencies can resolve from the plugin output directory.

## Failure behavior

A missing DLL, invalid DLL, incompatible API version, duplicate plugin ID, invalid check ID, or plugin-construction failure becomes an ERP Doctor plugin load issue. During `check`, those load issues become normal `Error` diagnostics instead of crashing the process.

If an individual plugin check throws, ERP Doctor reports the exception **type** but intentionally does not print the raw exception message.

## Plugin author safety checklist

Plugin authors should keep production checks read-only by default, never include passwords/tokens/connection strings in `Summary`, `Evidence`, or exception messages, obey cancellation tokens, bound network/database timeouts, and return evidence-backed suggestions rather than destructive automatic fixes.
