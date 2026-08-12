using System.Text.Json;

namespace ErpDoctor.Plugin.RabbitMq;

internal sealed record RabbitMqSettings(
    string BaseUrl,
    string Username,
    string PasswordEnvironmentVariable,
    string? VirtualHost,
    int RequestTimeoutSeconds,
    int MaxQueues,
    long ReadyMessagesWarning,
    long ReadyMessagesCritical,
    long UnackedMessagesWarning,
    long UnackedMessagesCritical,
    bool WarnOnNoConsumersWithReadyMessages,
    int MaxQueueEvidence)
{
    public static RabbitMqSettings From(JsonElement? configuration)
    {
        var root = configuration is { ValueKind: JsonValueKind.Object } element
            ? element
            : default;

        var readyWarning = Math.Clamp(ReadInt64(root, "readyMessagesWarning") ?? 1_000, 1, 1_000_000_000);
        var readyCritical = Math.Clamp(ReadInt64(root, "readyMessagesCritical") ?? 10_000, readyWarning, 1_000_000_000);
        var unackedWarning = Math.Clamp(ReadInt64(root, "unackedMessagesWarning") ?? 500, 1, 1_000_000_000);
        var unackedCritical = Math.Clamp(ReadInt64(root, "unackedMessagesCritical") ?? 5_000, unackedWarning, 1_000_000_000);

        return new RabbitMqSettings(
            (ReadString(root, "baseUrl") ?? "http://127.0.0.1:15672").TrimEnd('/'),
            ReadString(root, "username") ?? "guest",
            ReadString(root, "passwordEnvironmentVariable") ?? "ERP_DOCTOR_RABBITMQ_PASSWORD",
            ReadString(root, "virtualHost"),
            Math.Clamp(ReadInt32(root, "requestTimeoutSeconds") ?? 10, 1, 60),
            Math.Clamp(ReadInt32(root, "maxQueues") ?? 100, 1, 500),
            readyWarning,
            readyCritical,
            unackedWarning,
            unackedCritical,
            ReadBoolean(root, "warnOnNoConsumersWithReadyMessages") ?? false,
            Math.Clamp(ReadInt32(root, "maxQueueEvidence") ?? 10, 1, 50));
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

    private static long? ReadInt64(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out var value))
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
