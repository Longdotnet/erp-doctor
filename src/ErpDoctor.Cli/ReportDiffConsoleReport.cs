using ErpDoctor.Core;

internal static class ReportDiffConsoleReport
{
    public static void Write(
        DiagnosticReportDiff diff,
        string leftPath,
        string rightPath)
    {
        ArgumentNullException.ThrowIfNull(diff);

        Console.WriteLine("ERP Doctor report diff");
        Console.WriteLine(new string('─', 72));
        Console.WriteLine($"Left             {leftPath}");
        Console.WriteLine($"Right            {rightPath}");
        Console.WriteLine(
            $"Health score     {diff.Left.HealthScore} -> {diff.Right.HealthScore} " +
            $"({FormatDelta(diff.HealthScoreDelta)})");
        Console.WriteLine(
            $"Changes          improved {diff.Improved}, regressed {diff.Regressed}, " +
            $"changed {diff.Changed}, added {diff.Added}, removed {diff.Removed}, " +
            $"unchanged {diff.Unchanged}");
        Console.WriteLine($"Regression gate  {(diff.HasRegression ? "FAIL" : "PASS")} ({diff.RegressionCount})");

        var regressions = diff.Changes.Where(change => change.IsRegression).ToArray();
        var improvements = diff.Changes
            .Where(change =>
                !change.IsRegression &&
                change.Kind is DiagnosticReportChangeKind.Improved or DiagnosticReportChangeKind.Changed)
            .ToArray();
        var otherChanges = diff.Changes
            .Where(change =>
                !change.IsRegression &&
                change.Kind is not DiagnosticReportChangeKind.Improved and
                    not DiagnosticReportChangeKind.Changed and
                    not DiagnosticReportChangeKind.Unchanged)
            .ToArray();

        WriteSection("Regressions", regressions);
        WriteSection("Improvements / restored coverage", improvements);
        WriteSection("Other changes", otherChanges);

        if (regressions.Length == 0 && improvements.Length == 0 && otherChanges.Length == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No diagnostic status or coverage changes.");
        }
    }

    private static void WriteSection(
        string title,
        IReadOnlyList<DiagnosticReportCheckChange> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('─', 72));

        foreach (var change in changes)
        {
            Console.WriteLine(
                $"{ChangeMarker(change),-3} {change.CheckId}  " +
                $"{FormatStatus(change.BeforeStatus)} -> {FormatStatus(change.AfterStatus)}  " +
                $"[{change.Kind}]");

            if (!string.IsNullOrWhiteSpace(change.AfterSummary))
            {
                Console.WriteLine($"    {change.AfterSummary}");
            }
            else if (!string.IsNullOrWhiteSpace(change.BeforeSummary))
            {
                Console.WriteLine($"    {change.BeforeSummary}");
            }
        }
    }

    private static string ChangeMarker(DiagnosticReportCheckChange change) => change.Kind switch
    {
        DiagnosticReportChangeKind.Regressed => "!",
        DiagnosticReportChangeKind.Improved => "+",
        DiagnosticReportChangeKind.Changed => change.IsRegression ? "!" : "+",
        DiagnosticReportChangeKind.Added => change.IsRegression ? "!+" : "+",
        DiagnosticReportChangeKind.Removed => "!-",
        _ => "="
    };

    private static string FormatStatus(DiagnosticStatus? status) =>
        status?.ToString() ?? "missing";

    private static string FormatDelta(int delta) =>
        delta > 0 ? $"+{delta}" : delta.ToString();
}
