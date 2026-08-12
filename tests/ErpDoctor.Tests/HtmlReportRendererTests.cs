using ErpDoctor.Core;
using ErpDoctor.Reporting;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class HtmlReportRendererTests
{
    [Fact]
    public void Render_ProducesStandaloneReportWithScoreAndDiagnosis()
    {
        DiagnosticResult[] results =
        [
            new(
                "system.disk.c",
                "Disk C",
                DiagnosticStatus.Warning,
                "8.0 GB free",
                new Dictionary<string, string> { ["freePercent"] = "8.2" },
                ["Free disk space."],
                TimeSpan.FromMilliseconds(12))
        ];
        Diagnosis[] diagnoses =
        [
            new(
                DiagnosticStatus.Warning,
                "Low disk may affect the application",
                "Disk headroom is low.",
                ["8.0 GB free"],
                ["Review logs before deleting anything."])
        ];
        var report = DiagnosticReportFactory.Create(
            results,
            diagnoses,
            new DateTimeOffset(2026, 8, 12, 2, 30, 0, TimeSpan.Zero));

        var html = new HtmlReportRenderer().Render(report);

        Assert.Contains("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ERP Doctor", html);
        Assert.Contains("health score", html);
        Assert.Contains("Low disk may affect the application", html);
        Assert.Contains("freePercent", html);
        Assert.Contains("12 ms", html);
        Assert.DoesNotContain("<script src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link rel=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_HtmlEncodesDiagnosticContent()
    {
        DiagnosticResult[] results =
        [
            new(
                "http.hostile",
                "<script>alert('name')</script>",
                DiagnosticStatus.Critical,
                "failed <img src=x onerror=alert(1)>",
                new Dictionary<string, string>
                {
                    ["url"] = "https://erp.test/?a=1&b=<unsafe>"
                },
                ["Do not trust <raw> HTML."])
        ];
        var report = DiagnosticReportFactory.Create(results, []);

        var html = new HtmlReportRenderer().Render(report);

        Assert.DoesNotContain("<script>alert", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;img", html);
        Assert.Contains("&amp;b=", html);
    }
}
