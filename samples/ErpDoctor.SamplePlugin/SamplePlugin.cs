using System.Text.Json;
using ErpDoctor.PluginSdk;

namespace ErpDoctor.SamplePlugin;

public sealed class SamplePlugin : IErpDoctorPlugin
{
    public string Id => "sample";
    public string Name => "ERP Doctor Sample Plugin";
    public string Version => "0.1.0";

    public IReadOnlyList<IPluginCheck> CreateChecks(PluginContext context)
    {
        _ = context;
        return [new RequiredEnvironmentVariableCheck()];
    }
}

internal sealed class RequiredEnvironmentVariableCheck : IPluginCheck
{
    public string Id => "required-env";
    public string Name => "Required environment variable";
    public string Category => "configuration";

    public Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var variableName = ReadVariableName(context.Configuration) ?? "ERP_DOCTOR_SAMPLE_READY";
        var isSet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName));

        return Task.FromResult(new PluginDiagnosticResult(
            isSet ? PluginDiagnosticStatus.Healthy : PluginDiagnosticStatus.Warning,
            isSet
                ? $"Environment variable '{variableName}' is set."
                : $"Environment variable '{variableName}' is not set.",
            new Dictionary<string, string>
            {
                ["variable"] = variableName,
                ["isSet"] = isSet ? "true" : "false"
            },
            isSet
                ? null
                : [$"Set '{variableName}' if this environment requires it."]));
    }

    private static string? ReadVariableName(JsonElement? configuration)
    {
        if (configuration is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("requiredEnvironmentVariable", out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
