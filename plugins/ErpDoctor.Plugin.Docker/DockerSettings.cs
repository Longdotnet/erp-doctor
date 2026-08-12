using System.Text.Json;

namespace ErpDoctor.Plugin.Docker;

internal sealed record DockerSettings(
    string Executable,
    int CommandTimeoutSeconds,
    bool WarnOnStoppedContainers,
    int MaxContainerEvidence,
    IReadOnlyList<string> ExpectedContainers)
{
    public static DockerSettings From(JsonElement? configuration)
    {
        var root = configuration is { ValueKind: JsonValueKind.Object } element
            ? element
            : default;

        return new DockerSettings(
            ReadString(root, "dockerExecutable") ?? "docker",
            Math.Clamp(ReadInt32(root, "commandTimeoutSeconds") ?? 10, 1, 60),
            ReadBoolean(root, "warnOnStoppedContainers") ?? false,
            Math.Clamp(ReadInt32(root, "maxContainerEvidence") ?? 20, 1, 100),
            ReadStringArray(root, "expectedContainers")
                .Take(100)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
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
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value))
        {
            return null;
        }

        return value;
    }

    private static bool? ReadBoolean(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        return property.GetBoolean();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }
}
