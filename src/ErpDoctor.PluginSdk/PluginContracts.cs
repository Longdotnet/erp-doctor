using System.Text.Json;

namespace ErpDoctor.PluginSdk;

public static class PluginApi
{
    public const int CurrentVersion = 1;
}

public enum PluginDiagnosticStatus
{
    Healthy = 0,
    Info = 1,
    Warning = 2,
    Critical = 3,
    Skipped = 4,
    Error = 5
}

public sealed record PluginDiagnosticResult(
    PluginDiagnosticStatus Status,
    string Summary,
    IReadOnlyDictionary<string, string>? Evidence = null,
    IReadOnlyList<string>? Suggestions = null)
{
    public IReadOnlyDictionary<string, string> EvidenceOrEmpty =>
        Evidence ?? new Dictionary<string, string>();

    public IReadOnlyList<string> SuggestionsOrEmpty =>
        Suggestions ?? Array.Empty<string>();
}

public sealed record PluginContext(
    JsonElement? Configuration,
    string WorkingDirectory);

public interface IPluginCheck
{
    string Id { get; }
    string Name { get; }
    string Category { get; }

    Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken);
}

public interface IErpDoctorPlugin
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    int ApiVersion => PluginApi.CurrentVersion;

    IReadOnlyList<IPluginCheck> CreateChecks(PluginContext context);
}
