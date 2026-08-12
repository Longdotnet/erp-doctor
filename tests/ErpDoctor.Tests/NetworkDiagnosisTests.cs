using ErpDoctor.Core;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class NetworkDiagnosisTests
{
    [Fact]
    public void Diagnose_CorrelatesMatchingTcpFailureWithHttpOutage()
    {
        var results = new[]
        {
            new DiagnosticResult(
                "network.tcp.erp-api",
                "ERP API TCP",
                DiagnosticStatus.Critical,
                "TCP connection to api.internal:443 failed (ConnectionRefused).",
                new Dictionary<string, string>
                {
                    ["host"] = "api.internal",
                    ["port"] = "443",
                    ["latencyMs"] = "20"
                }),
            new DiagnosticResult(
                "http.erp-api",
                "ERP API",
                DiagnosticStatus.Critical,
                "HTTP request failed.",
                new Dictionary<string, string>
                {
                    ["url"] = "https://api.internal/health",
                    ["latencyMs"] = "25"
                })
        };

        var diagnoses = new DiagnosisEngine().Diagnose(results);

        Assert.Contains(
            diagnoses,
            diagnosis => diagnosis.Title.Contains("TCP reachability", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Diagnose_DoesNotCorrelateTcpFailureOnDifferentPort()
    {
        var results = new[]
        {
            new DiagnosticResult(
                "network.tcp.erp-db",
                "ERP DB TCP",
                DiagnosticStatus.Critical,
                "TCP connection failed.",
                new Dictionary<string, string>
                {
                    ["host"] = "api.internal",
                    ["port"] = "1433"
                }),
            new DiagnosticResult(
                "http.erp-api",
                "ERP API",
                DiagnosticStatus.Critical,
                "HTTP request failed.",
                new Dictionary<string, string>
                {
                    ["url"] = "https://api.internal/health"
                })
        };

        var diagnoses = new DiagnosisEngine().Diagnose(results);

        Assert.DoesNotContain(
            diagnoses,
            diagnosis => diagnosis.Title.Contains("TCP reachability", StringComparison.OrdinalIgnoreCase));
    }
}
