using ErpDoctor.Core;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class DiagnosisEngineTests
{
    [Fact]
    public void Diagnose_CorrelatesDiskIisAndHttpFailure()
    {
        var results = new[]
        {
            Result("system.disk.c", DiagnosticStatus.Critical, "C: 3% free"),
            Result("iis.apppool.erp-api", DiagnosticStatus.Critical, "AppPool state: Stopped"),
            Result("http.erp-api", DiagnosticStatus.Critical, "HTTP 503 in 20 ms")
        };

        var diagnoses = new DiagnosisEngine().Diagnose(results);

        var diagnosis = Assert.Single(diagnoses);
        Assert.Equal(DiagnosticStatus.Critical, diagnosis.Status);
        Assert.Contains("low disk space", diagnosis.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Diagnose_CorrelatesBlockingWithSlowHttp()
    {
        var results = new[]
        {
            new DiagnosticResult(
                "sql.blocking",
                "SQL blocking",
                DiagnosticStatus.Warning,
                "1 blocked request"),
            new DiagnosticResult(
                "http.erp-api",
                "ERP API",
                DiagnosticStatus.Warning,
                "HTTP 200 in 2300 ms",
                new Dictionary<string, string> { ["latencyMs"] = "2300" })
        };

        var diagnoses = new DiagnosisEngine().Diagnose(results);

        Assert.Contains(
            diagnoses,
            diagnosis => diagnosis.Title.Contains("blocking", StringComparison.OrdinalIgnoreCase));
    }

    private static DiagnosticResult Result(
        string id,
        DiagnosticStatus status,
        string summary) =>
        new(id, id, status, summary);
}
