namespace ErpDoctor.Core;

public sealed record DiagnosticReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    DiagnosticStatus OverallStatus,
    int HealthScore,
    ReportSummary Summary,
    IReadOnlyList<DiagnosticResult> Results,
    IReadOnlyList<Diagnosis> Diagnoses);

public sealed record ReportSummary(
    int Total,
    int Healthy,
    int Info,
    int Warning,
    int Critical,
    int Skipped,
    int Error);

public static class DiagnosticReportFactory
{
    public const string CurrentSchemaVersion = "1.0";

    public static DiagnosticReport Create(
        IReadOnlyList<DiagnosticResult> results,
        IReadOnlyList<Diagnosis> diagnoses,
        DateTimeOffset? generatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(diagnoses);

        var summary = new ReportSummary(
            Total: results.Count,
            Healthy: results.Count(x => x.Status == DiagnosticStatus.Healthy),
            Info: results.Count(x => x.Status == DiagnosticStatus.Info),
            Warning: results.Count(x => x.Status == DiagnosticStatus.Warning),
            Critical: results.Count(x => x.Status == DiagnosticStatus.Critical),
            Skipped: results.Count(x => x.Status == DiagnosticStatus.Skipped),
            Error: results.Count(x => x.Status == DiagnosticStatus.Error));

        return new DiagnosticReport(
            CurrentSchemaVersion,
            generatedAtUtc ?? DateTimeOffset.UtcNow,
            CalculateOverallStatus(results, diagnoses),
            CalculateHealthScore(results),
            summary,
            results,
            diagnoses);
    }

    internal static int CalculateHealthScore(IReadOnlyList<DiagnosticResult> results)
    {
        var scored = results
            .Where(x => x.Status != DiagnosticStatus.Skipped)
            .Select(x => x.Status switch
            {
                DiagnosticStatus.Healthy => 100,
                DiagnosticStatus.Info => 90,
                DiagnosticStatus.Warning => 60,
                DiagnosticStatus.Critical => 0,
                DiagnosticStatus.Error => 0,
                _ => 0
            })
            .ToArray();

        return scored.Length == 0
            ? 100
            : (int)Math.Round(scored.Average(), MidpointRounding.AwayFromZero);
    }

    private static DiagnosticStatus CalculateOverallStatus(
        IReadOnlyList<DiagnosticResult> results,
        IReadOnlyList<Diagnosis> diagnoses)
    {
        var statuses = results.Select(x => x.Status)
            .Concat(diagnoses.Select(x => x.Status))
            .Where(x => x != DiagnosticStatus.Skipped)
            .ToArray();

        if (statuses.Contains(DiagnosticStatus.Error))
        {
            return DiagnosticStatus.Error;
        }

        if (statuses.Contains(DiagnosticStatus.Critical))
        {
            return DiagnosticStatus.Critical;
        }

        if (statuses.Contains(DiagnosticStatus.Warning))
        {
            return DiagnosticStatus.Warning;
        }

        if (statuses.Contains(DiagnosticStatus.Info))
        {
            return DiagnosticStatus.Info;
        }

        return DiagnosticStatus.Healthy;
    }
}
