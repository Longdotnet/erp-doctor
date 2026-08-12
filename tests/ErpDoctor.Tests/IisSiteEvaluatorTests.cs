using ErpDoctor.Core;
using ErpDoctor.Infrastructure.IisDiagnostics;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class IisSiteEvaluatorTests
{
    [Fact]
    public void Evaluate_HealthyWhenSitePathAndExpectedBindingsMatch()
    {
        var site = new IisSiteOptions
        {
            Name = "ERP Site",
            ExpectedBindings = ["HTTPS:*:443:ERP.EXAMPLE.COM"]
        };
        var snapshot = new IisSiteSnapshot(
            "Started",
            @"D:\Apps\ERP",
            ["https:*:443:erp.example.com", "http:*:80:"]);

        var result = IisSiteEvaluator.Evaluate(site, snapshot, physicalPathExists: true);

        Assert.Equal(DiagnosticStatus.Healthy, result.Status);
        Assert.Equal("iis.site.erp-site", result.CheckId);
        Assert.Contains("2 binding(s)", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("missingBindings", result.EvidenceOrEmpty.Keys);
    }

    [Fact]
    public void Evaluate_CriticalWhenSiteIsStopped()
    {
        var site = new IisSiteOptions { Name = "ERP Site" };
        var snapshot = new IisSiteSnapshot(
            "Stopped",
            @"D:\Apps\ERP",
            ["http:*:80:"]);

        var result = IisSiteEvaluator.Evaluate(site, snapshot, physicalPathExists: true);

        Assert.Equal(DiagnosticStatus.Critical, result.Status);
        Assert.Contains("state Stopped", result.Summary, StringComparison.Ordinal);
        Assert.Contains(result.SuggestionsOrEmpty, suggestion =>
            suggestion.Contains("site is stopped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_CriticalWhenExpectedBindingIsMissing()
    {
        var site = new IisSiteOptions
        {
            Name = "ERP Site",
            ExpectedBindings = ["https:*:443:erp.example.com"]
        };
        var snapshot = new IisSiteSnapshot(
            "Started",
            @"D:\Apps\ERP",
            ["http:*:80:"]);

        var result = IisSiteEvaluator.Evaluate(site, snapshot, physicalPathExists: true);

        Assert.Equal(DiagnosticStatus.Critical, result.Status);
        Assert.Equal(
            "https:*:443:erp.example.com",
            result.EvidenceOrEmpty["missingBindings"]);
    }

    [Fact]
    public void Evaluate_CriticalWhenPhysicalPathIsMissing()
    {
        var site = new IisSiteOptions { Name = "ERP Site" };
        var snapshot = new IisSiteSnapshot(
            "Started",
            @"D:\Missing\ERP",
            ["http:*:80:"]);

        var result = IisSiteEvaluator.Evaluate(site, snapshot, physicalPathExists: false);

        Assert.Equal(DiagnosticStatus.Critical, result.Status);
        Assert.Contains("physical path missing", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_CanDisablePhysicalPathCheck()
    {
        var site = new IisSiteOptions
        {
            Name = "ERP Site",
            CheckPhysicalPath = false
        };
        var snapshot = new IisSiteSnapshot(
            "Started",
            string.Empty,
            ["http:*:80:"]);

        var result = IisSiteEvaluator.Evaluate(site, snapshot, physicalPathExists: false);

        Assert.Equal(DiagnosticStatus.Healthy, result.Status);
    }
}
