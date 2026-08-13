using System.Text.Json;
using ErpDoctor.Core;
using ErpDoctor.Reporting;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class DiagnosticReportDiffTests
{
    [Fact]
    public void Create_ClassifiesStatusCoverageAndAddedCheckChanges()
    {
        var left = CreateReport(
        [
            Result("system.cpu", DiagnosticStatus.Healthy),
            Result("sql.blocking", DiagnosticStatus.Critical),
            Result("http.api", DiagnosticStatus.Skipped),
            Result("network.tcp.api", DiagnosticStatus.Healthy),
            Result("plugin.redis.connectivity", DiagnosticStatus.Healthy),
            Result("system.memory", DiagnosticStatus.Healthy)
        ]);
        var right = CreateReport(
        [
            Result("system.cpu", DiagnosticStatus.Warning),
            Result("sql.blocking", DiagnosticStatus.Healthy),
            Result("http.api", DiagnosticStatus.Healthy),
            Result("network.tcp.api", DiagnosticStatus.Skipped),
            Result("system.memory", DiagnosticStatus.Healthy),
            Result("eventlog.errors", DiagnosticStatus.Critical),
            Result("system.disk.c", DiagnosticStatus.Healthy)
        ]);

        var diff = DiagnosticReportDiffFactory.Create(left, right);

        Assert.Equal(1, diff.Improved);
        Assert.Equal(1, diff.Regressed);
        Assert.Equal(2, diff.Changed);
        Assert.Equal(2, diff.Added);
        Assert.Equal(1, diff.Removed);
        Assert.Equal(1, diff.Unchanged);
        Assert.Equal(4, diff.RegressionCount);
        Assert.True(diff.HasRegression);

        AssertChange(
            diff,
            "system.cpu",
            DiagnosticReportChangeKind.Regressed,
            DiagnosticStatus.Healthy,
            DiagnosticStatus.Warning,
            isRegression: true);
        AssertChange(
            diff,
            "sql.blocking",
            DiagnosticReportChangeKind.Improved,
            DiagnosticStatus.Critical,
            DiagnosticStatus.Healthy,
            isRegression: false);
        AssertChange(
            diff,
            "http.api",
            DiagnosticReportChangeKind.Changed,
            DiagnosticStatus.Skipped,
            DiagnosticStatus.Healthy,
            isRegression: false);
        AssertChange(
            diff,
            "network.tcp.api",
            DiagnosticReportChangeKind.Changed,
            DiagnosticStatus.Healthy,
            DiagnosticStatus.Skipped,
            isRegression: true);
        AssertChange(
            diff,
            "plugin.redis.connectivity",
            DiagnosticReportChangeKind.Removed,
            DiagnosticStatus.Healthy,
            null,
            isRegression: true);
        AssertChange(
            diff,
            "eventlog.errors",
            DiagnosticReportChangeKind.Added,
            null,
            DiagnosticStatus.Critical,
            isRegression: true);
        AssertChange(
            diff,
            "system.disk.c",
            DiagnosticReportChangeKind.Added,
            null,
            DiagnosticStatus.Healthy,
            isRegression: false);
    }

    [Fact]
    public void Create_ReportsHealthScoreDelta()
    {
        var left = CreateReport(
        [
            Result("system.cpu", DiagnosticStatus.Healthy),
            Result("system.memory", DiagnosticStatus.Healthy)
        ]);
        var right = CreateReport(
        [
            Result("system.cpu", DiagnosticStatus.Warning),
            Result("system.memory", DiagnosticStatus.Healthy)
        ]);

        var diff = DiagnosticReportDiffFactory.Create(left, right);

        Assert.Equal(100, diff.Left.HealthScore);
        Assert.Equal(80, diff.Right.HealthScore);
        Assert.Equal(-20, diff.HealthScoreDelta);
    }

    [Fact]
    public void Create_RejectsUnsupportedReportSchema()
    {
        var valid = CreateReport([Result("system.cpu", DiagnosticStatus.Healthy)]);
        var unsupported = valid with { SchemaVersion = "2.0" };

        var exception = Assert.Throws<ArgumentException>(
            () => DiagnosticReportDiffFactory.Create(unsupported, valid));

        Assert.Contains("Left report schema '2.0' is not supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsDuplicateCheckIdsCaseInsensitively()
    {
        var duplicate = DiagnosticReportFactory.Create(
        [
            Result("system.cpu", DiagnosticStatus.Healthy),
            Result("SYSTEM.CPU", DiagnosticStatus.Warning)
        ],
        []);
        var valid = CreateReport([Result("system.cpu", DiagnosticStatus.Healthy)]);

        var exception = Assert.Throws<ArgumentException>(
            () => DiagnosticReportDiffFactory.Create(duplicate, valid));

        Assert.Contains("duplicate check ID", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonSerializers_RoundTripReportAndEmitStableDiffSchema()
    {
        var left = CreateReport([Result("system.cpu", DiagnosticStatus.Healthy)]);
        var right = CreateReport([Result("system.cpu", DiagnosticStatus.Warning)]);
        var reportJson = DiagnosticJsonReportSerializer.Serialize(left);
        var deserialized = DiagnosticJsonReportSerializer.Deserialize(reportJson);
        var diff = DiagnosticReportDiffFactory.Create(deserialized, right);

        var json = DiagnosticReportDiffJsonSerializer.Serialize(diff);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(-40, root.GetProperty("healthScoreDelta").GetInt32());
        Assert.True(root.GetProperty("hasRegression").GetBoolean());
        Assert.Equal(
            "regressed",
            root.GetProperty("changes")[0].GetProperty("kind").GetString());
        Assert.Equal(
            "warning",
            root.GetProperty("changes")[0].GetProperty("afterStatus").GetString());
        Assert.DoesNotContain("\n", json, StringComparison.Ordinal);
    }

    private static DiagnosticReport CreateReport(IReadOnlyList<DiagnosticResult> results) =>
        DiagnosticReportFactory.Create(
            results,
            [],
            new DateTimeOffset(2026, 8, 13, 4, 0, 0, TimeSpan.Zero));

    private static DiagnosticResult Result(string checkId, DiagnosticStatus status) =>
        new(
            checkId,
            checkId,
            status,
            $"{checkId} is {status}.");

    private static void AssertChange(
        DiagnosticReportDiff diff,
        string checkId,
        DiagnosticReportChangeKind kind,
        DiagnosticStatus? beforeStatus,
        DiagnosticStatus? afterStatus,
        bool isRegression)
    {
        var change = Assert.Single(
            diff.Changes,
            change => string.Equals(change.CheckId, checkId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(kind, change.Kind);
        Assert.Equal(beforeStatus, change.BeforeStatus);
        Assert.Equal(afterStatus, change.AfterStatus);
        Assert.Equal(isRegression, change.IsRegression);
    }
}
