using System.Text.Json;

namespace ErpDoctor.Plugin.Nginx;

internal sealed record NginxSettings(
    string Executable,
    string? ConfigPath,
    int CommandTimeoutSeconds,
    double LoadPerCpuWarning,
    double LoadPerCpuCritical)
{
    public static NginxSettings From(JsonElement? configuration)
    {
        var root = configuration is { ValueKind: JsonValueKind.Object } element
            ? element
            : default;

        var warning = Math.Clamp(ReadDouble(root, "loadPerCpuWarning") ?? 1.0d, 0.1d, 100d);
        var critical = Math.Clamp(ReadDouble(root, "loadPerCpuCritical") ?? 2.0d, warning, 200d);

        return new NginxSettings(
            ReadString(root, "nginxExecutable") ?? "nginx",
            ReadString(root, "configPath"),
            Math.Clamp(ReadInt32(root, "commandTimeoutSeconds") ?? 10, 1, 60),
            warning,
            critical);
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int? ReadInt32(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }

    private static double? ReadDouble(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out var value) &&
            !double.IsNaN(value) &&
            !double.IsInfinity(value))
        {
            return value;
        }

        return null;
    }
}
