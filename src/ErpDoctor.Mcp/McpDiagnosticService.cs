using ErpDoctor.Core;
using ErpDoctor.Infrastructure;
using ErpDoctor.PluginHost;

namespace ErpDoctor.Mcp;

public sealed class McpDiagnosticService(string configPath)
{
    private static readonly IReadOnlyDictionary<string, string?> ScopeCategories =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["check"] = null,
            ["system"] = "system",
            ["sql"] = "sql",
            ["http"] = "http",
            ["network"] = "network",
            ["iis"] = "iis",
            ["eventlog"] = "eventlog",
            ["plugin"] = "plugin"
        };

    private readonly string _configPath = Path.GetFullPath(configPath);

    public async Task<DiagnosticReport> RunAsync(
        string scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scope) || !ScopeCategories.TryGetValue(scope, out var category))
        {
            throw new ArgumentException(
                "Scope must be one of: check, system, sql, http, network, iis, eventlog, plugin.",
                nameof(scope));
        }

        var options = ErpDoctorOptions.Load(_configPath);
        var pluginDiscovery = ShouldLoadPlugins(scope)
            ? new PluginLoader().Load(options.Plugins, GetConfigDirectory())
            : EmptyPluginDiscovery();

        var checks = BuiltInDiagnosticCheckCatalog.Create(options)
            .Concat(pluginDiscovery.DiagnosticChecks)
            .ToArray();
        var runner = new DiagnosticRunner(checks);
        var results = await runner.RunAsync(
            new DiagnosticContext(options),
            category,
            cancellationToken);

        var diagnoses = scope.Equals("check", StringComparison.OrdinalIgnoreCase)
            ? new DiagnosisEngine().Diagnose(results)
            : Array.Empty<Diagnosis>();

        return DiagnosticReportFactory.Create(results, diagnoses);
    }

    private string GetConfigDirectory() =>
        Path.GetDirectoryName(_configPath) ?? Environment.CurrentDirectory;

    private static bool ShouldLoadPlugins(string scope) =>
        scope.Equals("check", StringComparison.OrdinalIgnoreCase) ||
        scope.Equals("plugin", StringComparison.OrdinalIgnoreCase);

    private static PluginDiscovery EmptyPluginDiscovery() =>
        new(Array.Empty<LoadedPlugin>(), Array.Empty<PluginLoadIssue>());
}
