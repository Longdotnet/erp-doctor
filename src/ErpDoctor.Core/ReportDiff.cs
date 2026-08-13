namespace ErpDoctor.Core;

public enum DiagnosticReportChangeKind
{
    Unchanged = 0,
    Improved = 1,
    Regressed = 2,
    Changed = 3,
    Added = 4,
    Removed = 5
}

public sealed record DiagnosticReportSnapshot(
    string ReportSchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    DiagnosticStatus OverallStatus,
    int HealthScore,
    int TotalChecks);

public sealed record DiagnosticReportCheckChange(
    string CheckId,
    string Name,
    DiagnosticReportChangeKind Kind,
    DiagnosticStatus? BeforeStatus,
    DiagnosticStatus? AfterStatus,
    string? BeforeSummary,
    string? AfterSummary,
    bool IsRegression);

public sealed record DiagnosticReportDiff(
    string SchemaVersion,
    DiagnosticReportSnapshot Left,
    DiagnosticReportSnapshot Right,
    int HealthScoreDelta,
    int Improved,
    int Regressed,
    int Changed,
    int Added,
    int Removed,
    int Unchanged,
    int RegressionCount,
    bool HasRegression,
    IReadOnlyList<DiagnosticReportCheckChange> Changes);

public static class DiagnosticReportDiffFactory
{
    public const string CurrentSchemaVersion = "1.0";

    public static DiagnosticReportDiff Create(
        DiagnosticReport left,
        DiagnosticReport right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        ValidateReportSchema(left, "Left");
        ValidateReportSchema(right, "Right");

        var leftById = BuildUniqueCheckMap(left, "Left");
        var rightById = BuildUniqueCheckMap(right, "Right");
        var checkIds = leftById.Keys
            .Union(rightById.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var changes = new List<DiagnosticReportCheckChange>(checkIds.Length);
        foreach (var checkId in checkIds)
        {
            leftById.TryGetValue(checkId, out var before);
            rightById.TryGetValue(checkId, out var after);
            changes.Add(CompareCheck(before, after));
        }

        var regressionCount = changes.Count(change => change.IsRegression);
        return new DiagnosticReportDiff(
            CurrentSchemaVersion,
            CreateSnapshot(left),
            CreateSnapshot(right),
            right.HealthScore - left.HealthScore,
            changes.Count(change => change.Kind == DiagnosticReportChangeKind.Improved),
            changes.Count(change => change.Kind == DiagnosticReportChangeKind.Regressed),
            changes.Count(change => change.Kind == DiagnosticReportChangeKind.Changed),
            changes.Count(change => change.Kind == DiagnosticReportChangeKind.Added),
            changes.Count(change => change.Kind == DiagnosticReportChangeKind.Removed),
            changes.Count(change => change.Kind == DiagnosticReportChangeKind.Unchanged),
            regressionCount,
            regressionCount > 0,
            changes);
    }

    private static DiagnosticReportSnapshot CreateSnapshot(DiagnosticReport report) =>
        new(
            report.SchemaVersion,
            report.GeneratedAtUtc,
            report.OverallStatus,
            report.HealthScore,
            report.Results.Count);

    private static Dictionary<string, DiagnosticResult> BuildUniqueCheckMap(
        DiagnosticReport report,
        string side)
    {
        var result = new Dictionary<string, DiagnosticResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var check in report.Results)
        {
            if (string.IsNullOrWhiteSpace(check.CheckId))
            {
                throw new ArgumentException($"{side} report contains a diagnostic with an empty check ID.");
            }

            if (!result.TryAdd(check.CheckId, check))
            {
                throw new ArgumentException(
                    $"{side} report contains duplicate check ID '{check.CheckId}'.");
            }
        }

        return result;
    }

    private static void ValidateReportSchema(DiagnosticReport report, string side)
    {
        if (!string.Equals(
            report.SchemaVersion,
            DiagnosticReportFactory.CurrentSchemaVersion,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{side} report schema '{report.SchemaVersion}' is not supported. " +
                $"Expected '{DiagnosticReportFactory.CurrentSchemaVersion}'.");
        }
    }

    private static DiagnosticReportCheckChange CompareCheck(
        DiagnosticResult? before,
        DiagnosticResult? after)
    {
        if (before is null && after is not null)
        {
            var regression = IsProblemStatus(after.Status);
            return new DiagnosticReportCheckChange(
                after.CheckId,
                after.Name,
                DiagnosticReportChangeKind.Added,
                null,
                after.Status,
                null,
                after.Summary,
                regression);
        }

        if (before is not null && after is null)
        {
            return new DiagnosticReportCheckChange(
                before.CheckId,
                before.Name,
                DiagnosticReportChangeKind.Removed,
                before.Status,
                null,
                before.Summary,
                null,
                true);
        }

        if (before is null || after is null)
        {
            throw new InvalidOperationException("A report diff check must exist on at least one side.");
        }

        if (before.Status == after.Status)
        {
            return new DiagnosticReportCheckChange(
                after.CheckId,
                after.Name,
                DiagnosticReportChangeKind.Unchanged,
                before.Status,
                after.Status,
                before.Summary,
                after.Summary,
                false);
        }

        if (after.Status == DiagnosticStatus.Skipped)
        {
            return new DiagnosticReportCheckChange(
                after.CheckId,
                after.Name,
                DiagnosticReportChangeKind.Changed,
                before.Status,
                after.Status,
                before.Summary,
                after.Summary,
                true);
        }

        if (before.Status == DiagnosticStatus.Skipped)
        {
            return new DiagnosticReportCheckChange(
                after.CheckId,
                after.Name,
                DiagnosticReportChangeKind.Changed,
                before.Status,
                after.Status,
                before.Summary,
                after.Summary,
                false);
        }

        var regressed = Severity(after.Status) > Severity(before.Status);
        return new DiagnosticReportCheckChange(
            after.CheckId,
            after.Name,
            regressed
                ? DiagnosticReportChangeKind.Regressed
                : DiagnosticReportChangeKind.Improved,
            before.Status,
            after.Status,
            before.Summary,
            after.Summary,
            regressed);
    }

    private static bool IsProblemStatus(DiagnosticStatus status) =>
        status is DiagnosticStatus.Warning or DiagnosticStatus.Critical or DiagnosticStatus.Error;

    private static int Severity(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => 0,
        DiagnosticStatus.Info => 1,
        DiagnosticStatus.Warning => 2,
        DiagnosticStatus.Critical => 3,
        DiagnosticStatus.Error => 4,
        DiagnosticStatus.Skipped => throw new InvalidOperationException(
            "Skipped status must be handled before severity comparison."),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown diagnostic status.")
    };
}
