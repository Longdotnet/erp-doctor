using System.Text.Json;

namespace ErpDoctor.Plugin.Redis;

internal sealed record RedisSettings(
    string Executable,
    string Host,
    int Port,
    string? Username,
    string? PasswordEnvironmentVariable,
    bool UseTls,
    string? CaCertificatePath,
    int CommandTimeoutSeconds,
    double MemoryWarningPercent,
    double MemoryCriticalPercent,
    int ReplicaLagWarningSeconds,
    int ReplicaLagCriticalSeconds)
{
    public static RedisSettings From(JsonElement? configuration)
    {
        var root = configuration is { ValueKind: JsonValueKind.Object } element
            ? element
            : default;

        var memoryWarning = Math.Clamp(ReadDouble(root, "memoryWarningPercent") ?? 80d, 1d, 100d);
        var memoryCritical = Math.Clamp(ReadDouble(root, "memoryCriticalPercent") ?? 90d, memoryWarning, 100d);
        var lagWarning = Math.Clamp(ReadInt32(root, "replicaLagWarningSeconds") ?? 10, 1, 3600);
        var lagCritical = Math.Clamp(ReadInt32(root, "replicaLagCriticalSeconds") ?? 30, lagWarning, 86400);

        return new RedisSettings(
            ReadString(root, "redisCliExecutable") ?? "redis-cli",
            ReadString(root, "host") ?? "127.0.0.1",
            Math.Clamp(ReadInt32(root, "port") ?? 6379, 1, 65535),
            ReadString(root, "username"),
            ReadString(root, "passwordEnvironmentVariable"),
            ReadBoolean(root, "tls") ?? false,
            ReadString(root, "caCertificatePath"),
            Math.Clamp(ReadInt32(root, "commandTimeoutSeconds") ?? 10, 1, 60),
            memoryWarning,
            memoryCritical,
            lagWarning,
            lagCritical);
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

    private static double? ReadDouble(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDouble(out var value))
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
}
