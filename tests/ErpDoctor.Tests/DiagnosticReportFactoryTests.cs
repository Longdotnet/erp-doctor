using ErpDoctor.Core;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class DiagnosticReportFactoryTests
{
    [Fact]
    public void Create_ComputesSummaryHealthScoreAndOverallStatus()
    {
        DiagnosticResult[] results =
        [
            Result("healthy", DiagnosticStatus.Healthy),
            Result("info", DiagnosticStatus.Info),
            Result("warning", DiagnosticStatus.Warning),
            Result("critical", DiagnosticStatus.Critical),
            Result("skipped", DiagnosticStatus.Skipped)
        ];

        var generatedAt = new DateTimeOffset(2026, 8, 12, 2, 30, 0, TimeSpan.Zero);
        var report = DiagnosticReportFactory.Create(results, [], generatedAt);

        Assert.Equal(DiagnosticReportFactory.CurrentSchemaVersion, report.SchemaVersion);
        Assert.Equal(generatedAt, report.GeneratedAtUtc);
        Assert.Equal(DiagnosticStatus.Critical, report.OverallStatus);
        Assert.Equal(63, report.HealthScore);
        Assert.Equal(5, report.Summary.Total);
        Assert.Equal(1, report.Summary.Healthy);
        Assert.Equal(1, report.Summary.Info);
        Assert.Equal(1, report.Summary.Warning);
        Assert.Equal(1, report.Summary.Critical);
        Assert.Equal(1, report.Summary.Skipped);
        Assert.Equal(0, report.Summary.Error);
    }

    [Fact]
    public void Create_AllSkipped_ReturnsHealthyScoreWithoutPenalizingUnavailableChecks()
    {
        DiagnosticResult[] results =
        [
            Result("sql", DiagnosticStatus.Skipped),
            Result("iis", DiagnosticStatus.Skipped)
        ];

        var report = DiagnosticReportFactory.Create(results, []);

        Assert.Equal(100, report.HealthScore);
        Assert.Equal(DiagnosticStatus.Healthy, report.OverallStatus);
    }

    [Fact]
    public void Create_DiagnosisCanRaiseOverallStatus()
    {
        DiagnosticResult[] results = [Result("http", DiagnosticStatus.Healthy)];
        Diagnosis[] diagnoses =
        [
            new(
                DiagnosticStatus.Warning,
                "Correlated warning",
                "A correlated condition was detected.",
                ["evidence"],
                ["action"])
        ];

        var report = DiagnosticReportFactory.Create(results, diagnoses);

        Assert.Equal(DiagnosticStatus.Warning, report.OverallStatus);
        Assert.Equal(100, report.HealthScore);
    }

    private static DiagnosticResult Result(string id, DiagnosticStatus status) =>
        new(id, id, status, id);
}
