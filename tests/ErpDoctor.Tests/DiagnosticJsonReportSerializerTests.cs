using System.Text.Json;
using ErpDoctor.Core;
using ErpDoctor.Reporting;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class DiagnosticJsonReportSerializerTests
{
    [Fact]
    public void Serialize_UsesStableCamelCaseSchemaAndStringEnums()
    {
        var report = DiagnosticReportFactory.Create(
        [
            new DiagnosticResult(
                "network.tcp.erp-api",
                "ERP API TCP",
                DiagnosticStatus.Warning,
                "TCP latency is elevated.",
                new Dictionary<string, string>
                {
                    ["host"] = "erp.internal",
                    ["port"] = "443"
                })
        ],
        [],
        new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero));

        var json = DiagnosticJsonReportSerializer.Serialize(report);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("warning", root.GetProperty("overallStatus").GetString());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("warning").GetInt32());
        Assert.Equal(
            "warning",
            root.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(
            "erp.internal",
            root.GetProperty("results")[0].GetProperty("evidence").GetProperty("host").GetString());
        Assert.DoesNotContain("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_IndentedModePreservesSameDocumentShape()
    {
        var report = DiagnosticReportFactory.Create(
            [new DiagnosticResult("system.cpu", "System CPU", DiagnosticStatus.Healthy, "CPU is healthy.")],
            []);

        var compact = DiagnosticJsonReportSerializer.Serialize(report);
        var indented = DiagnosticJsonReportSerializer.Serialize(report, writeIndented: true);

        using var compactDocument = JsonDocument.Parse(compact);
        using var indentedDocument = JsonDocument.Parse(indented);

        Assert.Equal(
            compactDocument.RootElement.GetProperty("schemaVersion").GetString(),
            indentedDocument.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(
            compactDocument.RootElement.GetProperty("results").GetArrayLength(),
            indentedDocument.RootElement.GetProperty("results").GetArrayLength());
        Assert.Contains("\n", indented, StringComparison.Ordinal);
    }
}
