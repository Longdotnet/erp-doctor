using System.Globalization;
using ErpDoctor.PluginSdk;

namespace ErpDoctor.Plugin.Postgres;

internal sealed record PostgresLongRunningSnapshot(
    int Pid,
    double DurationSeconds);

internal sealed record PostgresBlockingSnapshot(
    int BlockedPid,
    IReadOnlyList<int> BlockingPids,
    double AgeSeconds,
    string WaitEvent);

internal static class PostgresDatabaseSizeEvaluator
{
    public static PluginDiagnosticResult Evaluate(long sizeBytes, double warningGb)
    {
        var sizeGb = sizeBytes / 1024d / 1024d / 1024d;
        var warning = sizeGb >= warningGb;

        return new PluginDiagnosticResult(
            warning ? PluginDiagnosticStatus.Warning : PluginDiagnosticStatus.Healthy,
            warning
                ? $"Database size is {sizeGb:F2} GB, at or above the {warningGb:F2} GB warning threshold."
                : $"Database size is {sizeGb:F2} GB.",
            new Dictionary<string, string>
            {
                ["databaseSizeGb"] = sizeGb.ToString("F2", CultureInfo.InvariantCulture),
                ["warningThresholdGb"] = warningGb.ToString("F2", CultureInfo.InvariantCulture)
            },
            warning
                ? ["Inspect database/table growth and retention before disk pressure becomes an outage."]
                : null);
    }
}

internal static class PostgresLongRunningEvaluator
{
    public static PluginDiagnosticResult Evaluate(
        IReadOnlyList<PostgresLongRunningSnapshot> snapshots,
        int thresholdSeconds)
    {
        if (snapshots.Count == 0)
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Healthy,
                $"No active PostgreSQL queries have run for {thresholdSeconds}s or longer.",
                new Dictionary<string, string>
                {
                    ["longRunningCount"] = "0",
                    ["thresholdSeconds"] = thresholdSeconds.ToString(CultureInfo.InvariantCulture)
                });
        }

        var longest = snapshots.Max(item => item.DurationSeconds);
        var evidence = new Dictionary<string, string>
        {
            ["longRunningCount"] = snapshots.Count.ToString(CultureInfo.InvariantCulture),
            ["longestSeconds"] = longest.ToString("F1", CultureInfo.InvariantCulture),
            ["thresholdSeconds"] = thresholdSeconds.ToString(CultureInfo.InvariantCulture),
            ["pids"] = string.Join(",", snapshots.Select(item => item.Pid).Take(20))
        };

        return new PluginDiagnosticResult(
            PluginDiagnosticStatus.Warning,
            $"{snapshots.Count} active PostgreSQL quer{(snapshots.Count == 1 ? "y has" : "ies have")} run for {thresholdSeconds}s or longer; longest {longest:F1}s.",
            evidence,
            [
                "Inspect the listed backend PIDs in PostgreSQL monitoring tools.",
                "ERP Doctor intentionally does not include SQL text in plugin evidence."
            ]);
    }
}

internal static class PostgresBlockingEvaluator
{
    public static PluginDiagnosticResult Evaluate(
        IReadOnlyList<PostgresBlockingSnapshot> snapshots,
        int thresholdSeconds)
    {
        if (snapshots.Count == 0)
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Healthy,
                $"No PostgreSQL sessions have been blocked for {thresholdSeconds}s or longer.",
                new Dictionary<string, string>
                {
                    ["blockedCount"] = "0",
                    ["thresholdSeconds"] = thresholdSeconds.ToString(CultureInfo.InvariantCulture)
                });
        }

        var oldest = snapshots.Max(item => item.AgeSeconds);
        var evidence = new Dictionary<string, string>
        {
            ["blockedCount"] = snapshots.Count.ToString(CultureInfo.InvariantCulture),
            ["oldestBlockedSeconds"] = oldest.ToString("F1", CultureInfo.InvariantCulture),
            ["thresholdSeconds"] = thresholdSeconds.ToString(CultureInfo.InvariantCulture)
        };

        for (var i = 0; i < Math.Min(10, snapshots.Count); i++)
        {
            var item = snapshots[i];
            evidence[$"blocked{i + 1}"] =
                $"pid={item.BlockedPid}; blockers={string.Join(',', item.BlockingPids)}; age={item.AgeSeconds:F1}s; wait={item.WaitEvent}";
        }

        return new PluginDiagnosticResult(
            PluginDiagnosticStatus.Warning,
            $"{snapshots.Count} PostgreSQL session(s) blocked for at least {thresholdSeconds}s; oldest {oldest:F1}s.",
            evidence,
            [
                "Inspect the blocked and blocking backend PIDs before terminating any session.",
                "ERP Doctor does not cancel queries or terminate PostgreSQL backends automatically."
            ]);
    }
}
