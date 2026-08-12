using System.Text.Json;

namespace ErpDoctor.Plugin.Postgres;

internal sealed record PostgresSettings(
    string ConnectionStringEnvironmentVariable,
    int ConnectionTimeoutSeconds,
    int CommandTimeoutSeconds,
    double DatabaseSizeWarningGb,
    int LongRunningWarningSeconds,
    int BlockingWarningSeconds)
{
    public static PostgresSettings From(JsonElement? configuration)
    {
        var root = configuration is { ValueKind: JsonValueKind.Object } element
            ? element
            : default;

        return new PostgresSettings(
            ReadString(root, "connectionStringEnvironmentVariable") ?? "ERP_DOCTOR_POSTGRES",
            Clamp(ReadInt32(root, "connectionTimeoutSeconds") ?? 5, 1, 30),
            Clamp(ReadInt32(root, "commandTimeoutSeconds") ?? 10, 1, 60),
            Clamp(ReadDouble(root, "databaseSizeWarningGb") ?? 20d, 0.1d, 100_000d),
            Clamp(ReadInt32(root, "longRunningWarningSeconds") ?? 30, 1, 86_400),
            Clamp(ReadInt32(root, "blockingWarningSeconds") ?? 10, 1, 86_400));
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
            !property.TryGetDouble(out var value) ||
            double.IsNaN(value) ||
            double.IsInfinity(value))
        {
            return null;
        }

        return value;
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
    private static double Clamp(double value, double min, double max) => Math.Clamp(value, min, max);
}
