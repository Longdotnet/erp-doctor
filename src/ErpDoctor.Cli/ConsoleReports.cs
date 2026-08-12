using ErpDoctor.Core;
using ErpDoctor.Infrastructure.SqlServerDiagnostics;

internal static class ConfigDriftConsoleReport
{
    public static void Write(ConfigDriftReport report)
    {
        Console.WriteLine();
        Console.WriteLine("ERP Doctor - Configuration Drift");
        Console.WriteLine(new string('─', 72));
        Console.WriteLine($"Left  : {report.LeftLabel}");
        Console.WriteLine($"Right : {report.RightLabel}");
        Console.WriteLine($"Drift : {report.Differences.Count} difference(s)");

        if (report.Differences.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No configuration drift detected.");
            return;
        }

        foreach (var difference in report.Differences)
        {
            Console.WriteLine();
            Console.WriteLine($"{GetSymbol(difference.Kind)} {difference.Path} ({Describe(difference.Kind)})");
            Console.WriteLine($"  left  : {difference.LeftValue}");
            Console.WriteLine($"  right : {difference.RightValue}");
            if (difference.IsSensitive)
            {
                Console.WriteLine("  note  : sensitive values are redacted; ERP Doctor does not hash or print them.");
            }
        }
    }

    private static string GetSymbol(ConfigDriftKind kind) => kind switch
    {
        ConfigDriftKind.Different => "~",
        ConfigDriftKind.TypeChanged => "!",
        ConfigDriftKind.MissingLeft => "+",
        ConfigDriftKind.MissingRight => "-",
        _ => "?"
    };

    private static string Describe(ConfigDriftKind kind) => kind switch
    {
        ConfigDriftKind.Different => "different",
        ConfigDriftKind.TypeChanged => "type changed",
        ConfigDriftKind.MissingLeft => "only on right",
        ConfigDriftKind.MissingRight => "only on left",
        _ => "unknown"
    };
}

internal static class GrowthConsoleReport
{
    public static void Write(
        SqlGrowthSnapshot current,
        SqlGrowthComparison? comparison,
        string historyPath)
    {
        Console.WriteLine();
        Console.WriteLine("ERP Doctor - Database Growth");
        Console.WriteLine(new string('─', 72));
        Console.WriteLine($"Database : {current.Server}/{current.Database}");
        Console.WriteLine($"Captured : {current.CapturedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine(
            $"Current  : {current.TotalSizeMb / 1024d:F2} GB total " +
            $"({current.DataSizeMb / 1024d:F2} GB data, {current.LogSizeMb / 1024d:F2} GB log)");

        if (comparison is null)
        {
            Console.WriteLine();
            Console.WriteLine("Baseline created. Run `erp-doctor growth` again later to calculate growth deltas.");
            Console.WriteLine($"History  : {historyPath}");
            return;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Since    : {comparison.PreviousCapturedAtUtc:yyyy-MM-dd HH:mm:ss} UTC " +
            $"({FormatInterval(comparison.Interval)})");
        Console.WriteLine($"Data     : {FormatDelta(comparison.DataDeltaMb)}");
        Console.WriteLine($"Log      : {FormatDelta(comparison.LogDeltaMb)}");
        Console.WriteLine($"Total    : {FormatDelta(comparison.TotalDeltaMb)}");
        if (comparison.TotalGrowthMbPerDay is { } perDay)
        {
            Console.WriteLine($"Rate     : {perDay:+0.0;-0.0;0.0} MB/day");
        }

        Console.WriteLine();
        Console.WriteLine("Table growth");
        Console.WriteLine(new string('─', 72));

        if (comparison.TableGrowth.Count == 0)
        {
            Console.WriteLine("No table-size changes detected in the captured set.");
        }
        else
        {
            foreach (var table in comparison.TableGrowth)
            {
                if (table.IsNewInCapturedSet)
                {
                    Console.WriteLine(
                        $"? {table.Name,-36} {table.CurrentReservedMb,10:F1} MB  new in captured set");
                    continue;
                }

                Console.WriteLine(
                    $"  {table.Name,-36} {table.ReservedDeltaMb,10:+0.0;-0.0;0.0} MB  " +
                    $"rows {table.RowDelta,12:+#,0;-#,0;0}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"History  : {historyPath}");
        Console.WriteLine("Note     : history is local ERP Doctor state; no history table is created in SQL Server.");
    }

    private static string FormatDelta(double value) =>
        $"{value:+0.0;-0.0;0.0} MB";

    private static string FormatInterval(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return $"{value.TotalDays:F1} days";
        }

        if (value.TotalHours >= 1)
        {
            return $"{value.TotalHours:F1} hours";
        }

        return $"{Math.Max(1, value.TotalMinutes):F0} minutes";
    }
}

internal static class ConsoleReport
{
    public static void Write(DiagnosticReport report)
    {
        var results = report.Results;
        var diagnoses = report.Diagnoses;

        Console.WriteLine();
        Console.WriteLine("ERP Doctor");
        Console.WriteLine(new string('─', 64));
        Console.WriteLine($"Health score: {report.HealthScore}/100 | Overall: {report.OverallStatus}");

        foreach (var group in results.GroupBy(GetCategory))
        {
            Console.WriteLine();
            Console.WriteLine(group.Key.ToUpperInvariant());
            Console.WriteLine(new string('─', 64));

            foreach (var result in group)
            {
                var oldColor = Console.ForegroundColor;
                Console.ForegroundColor = GetColor(result.Status);
                Console.Write(GetSymbol(result.Status));
                Console.ForegroundColor = oldColor;

                Console.WriteLine($" {result.Name,-30} {result.Summary}");

                foreach (var suggestion in result.SuggestionsOrEmpty)
                {
                    Console.WriteLine($"    -> {suggestion}");
                }
            }
        }

        if (diagnoses.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("DIAGNOSIS");
            Console.WriteLine(new string('─', 64));

            foreach (var diagnosis in diagnoses)
            {
                var oldColor = Console.ForegroundColor;
                Console.ForegroundColor = GetColor(diagnosis.Status);
                Console.WriteLine($"{diagnosis.Status.ToString().ToUpperInvariant()}: {diagnosis.Title}");
                Console.ForegroundColor = oldColor;

                Console.WriteLine(diagnosis.Explanation);
                foreach (var evidence in diagnosis.Evidence)
                {
                    Console.WriteLine($"  evidence: {evidence}");
                }

                foreach (var action in diagnosis.SuggestedActions)
                {
                    Console.WriteLine($"  -> {action}");
                }

                Console.WriteLine();
            }
        }

        var summary = report.Summary;
        Console.WriteLine();
        Console.WriteLine(
            $"Summary: {summary.Healthy} healthy | {summary.Info} info | {summary.Warning} warning | " +
            $"{summary.Critical} critical | {summary.Error} error | {summary.Skipped} skipped");
    }

    private static string GetCategory(DiagnosticResult result)
    {
        var index = result.CheckId.IndexOf('.');
        return index < 0 ? "general" : result.CheckId[..index];
    }

    private static string GetSymbol(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => "✓",
        DiagnosticStatus.Info => "i",
        DiagnosticStatus.Warning => "!",
        DiagnosticStatus.Critical => "✗",
        DiagnosticStatus.Skipped => "-",
        DiagnosticStatus.Error => "x",
        _ => "?"
    };

    private static ConsoleColor GetColor(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => ConsoleColor.Green,
        DiagnosticStatus.Info => ConsoleColor.Cyan,
        DiagnosticStatus.Warning => ConsoleColor.Yellow,
        DiagnosticStatus.Critical => ConsoleColor.Red,
        DiagnosticStatus.Skipped => ConsoleColor.DarkGray,
        DiagnosticStatus.Error => ConsoleColor.Magenta,
        _ => Console.ForegroundColor
    };
}
