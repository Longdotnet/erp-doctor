using System.Globalization;
using ErpDoctor.PluginSdk;

namespace ErpDoctor.Plugin.Nginx;

internal sealed record LinuxRuntimeSnapshot(
    string Distribution,
    string Version,
    double UptimeHours,
    double Load1,
    double Load5,
    double Load15,
    int ProcessorCount,
    double? MemoryAvailablePercent);

internal static class LinuxSnapshotParser
{
    public static LinuxRuntimeSnapshot Parse(
        string osRelease,
        string uptime,
        string loadavg,
        string meminfo,
        int processorCount)
    {
        var os = ParseKeyValueLines(osRelease);
        var distribution = GetValue(os, "PRETTY_NAME") ?? GetValue(os, "NAME") ?? "Linux";
        var version = GetValue(os, "VERSION_ID") ?? "unknown";

        var uptimeSeconds = ParseDoubleToken(uptime.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
        var loadParts = loadavg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var load1 = loadParts.Length > 0 ? ParseDoubleToken(loadParts[0]) : 0d;
        var load5 = loadParts.Length > 1 ? ParseDoubleToken(loadParts[1]) : 0d;
        var load15 = loadParts.Length > 2 ? ParseDoubleToken(loadParts[2]) : 0d;

        var memory = ParseMemInfo(meminfo);
        double? memoryAvailablePercent = null;
        if (memory.TryGetValue("MemTotal", out var totalKb) &&
            memory.TryGetValue("MemAvailable", out var availableKb) &&
            totalKb > 0)
        {
            memoryAvailablePercent = Math.Clamp(availableKb / totalKb * 100d, 0d, 100d);
        }

        return new LinuxRuntimeSnapshot(
            distribution,
            version,
            Math.Max(0d, uptimeSeconds / 3600d),
            Math.Max(0d, load1),
            Math.Max(0d, load5),
            Math.Max(0d, load15),
            Math.Max(1, processorCount),
            memoryAvailablePercent);
    }

    private static Dictionary<string, string> ParseKeyValueLines(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = line.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        return values;
    }

    private static Dictionary<string, double> ParseMemInfo(string content)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = line.IndexOf(':');
            if (index <= 0)
            {
                continue;
            }

            var key = line[..index].Trim();
            var raw = line[(index + 1)..].Trim();
            var token = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static double ParseDoubleToken(string? token) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0d;
}

internal static class LinuxSnapshotEvaluator
{
    public static PluginDiagnosticResult Evaluate(
        LinuxRuntimeSnapshot snapshot,
        NginxSettings settings)
    {
        var loadPerCpu = snapshot.Load1 / Math.Max(1, snapshot.ProcessorCount);
        var status = loadPerCpu >= settings.LoadPerCpuCritical
            ? PluginDiagnosticStatus.Critical
            : loadPerCpu >= settings.LoadPerCpuWarning
                ? PluginDiagnosticStatus.Warning
                : PluginDiagnosticStatus.Healthy;

        var evidence = new Dictionary<string, string>
        {
            ["distribution"] = snapshot.Distribution,
            ["version"] = snapshot.Version,
            ["uptimeHours"] = snapshot.UptimeHours.ToString("F1", CultureInfo.InvariantCulture),
            ["processorCount"] = snapshot.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            ["load1"] = snapshot.Load1.ToString("F2", CultureInfo.InvariantCulture),
            ["load5"] = snapshot.Load5.ToString("F2", CultureInfo.InvariantCulture),
            ["load15"] = snapshot.Load15.ToString("F2", CultureInfo.InvariantCulture),
            ["load1PerCpu"] = loadPerCpu.ToString("F2", CultureInfo.InvariantCulture)
        };

        if (snapshot.MemoryAvailablePercent is { } memoryAvailablePercent)
        {
            evidence["memoryAvailablePercent"] =
                memoryAvailablePercent.ToString("F1", CultureInfo.InvariantCulture);
        }

        var summary = status switch
        {
            PluginDiagnosticStatus.Critical =>
                $"Linux 1-minute load per CPU is {loadPerCpu:F2}, at or above the {settings.LoadPerCpuCritical:F2} critical threshold.",
            PluginDiagnosticStatus.Warning =>
                $"Linux 1-minute load per CPU is {loadPerCpu:F2}, at or above the {settings.LoadPerCpuWarning:F2} warning threshold.",
            _ =>
                $"Linux runtime looks stable; 1-minute load per CPU is {loadPerCpu:F2}."
        };

        IReadOnlyList<string>? suggestions = status switch
        {
            PluginDiagnosticStatus.Critical =>
            [
                "Inspect sustained CPU/I/O pressure and the busiest processes before restarting services.",
                "Compare 1/5/15-minute load values to distinguish a short spike from sustained pressure."
            ],
            PluginDiagnosticStatus.Warning =>
            [
                "Watch whether load remains elevated across the 5- and 15-minute averages."
            ],
            _ => null
        };

        return new PluginDiagnosticResult(status, summary, evidence, suggestions);
    }
}
