using ErpDoctor.Core;
using ErpDoctor.Infrastructure.SystemDiagnostics;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class SystemPressureTests
{
    [Fact]
    public void LinuxCpuParser_ParsesAggregateCpuCounters()
    {
        var parsed = LinuxCpuParser.TryParse(
            "cpu  100 20 30 400 50 6 7 8 0 0",
            out var times);

        Assert.True(parsed);
        Assert.Equal(450UL, times.Idle);
        Assert.Equal(621UL, times.Total);
    }

    [Theory]
    [InlineData(50, DiagnosticStatus.Healthy)]
    [InlineData(80, DiagnosticStatus.Warning)]
    [InlineData(95, DiagnosticStatus.Critical)]
    public void CpuPressureEvaluator_UsesConfiguredThresholds(
        double utilization,
        DiagnosticStatus expected)
    {
        var options = new SystemOptions
        {
            CpuWarningPercent = 80,
            CpuCriticalPercent = 95
        };

        var result = CpuPressureEvaluator.Evaluate(
            utilization,
            sampleMilliseconds: 250,
            processorCount: 8,
            options);

        Assert.Equal(expected, result.Status);
        Assert.Equal(utilization.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            result.EvidenceOrEmpty["utilizationPercent"]);
        Assert.Equal("8", result.EvidenceOrEmpty["processorCount"]);
    }

    [Fact]
    public void LinuxLoadAverageParser_AndEvaluator_NormalizeByProcessorCount()
    {
        Assert.True(LinuxLoadAverageParser.TryParse(
            "4.00 2.50 1.50 2/100 1234",
            out var snapshot));

        var result = LoadAverageEvaluator.Evaluate(
            snapshot,
            processorCount: 4,
            new SystemOptions
            {
                LoadPerCpuWarning = 1,
                LoadPerCpuCritical = 2
            });

        Assert.Equal(DiagnosticStatus.Warning, result.Status);
        Assert.Equal("4.00", result.EvidenceOrEmpty["load1"]);
        Assert.Equal("1.00", result.EvidenceOrEmpty["load1PerCpu"]);
    }

    [Fact]
    public void ProcessSnapshotFormatter_IsBoundedToResourceMetadata()
    {
        var rendered = ProcessSnapshotFormatter.Format(
        [
            new ProcessResourceSnapshot(12, "sqlservr", 2L * 1024 * 1024 * 1024),
            new ProcessResourceSnapshot(34, "erp-api", 512L * 1024 * 1024)
        ]);

        Assert.Contains("12:sqlservr:2048.0MB", rendered, StringComparison.Ordinal);
        Assert.Contains("34:erp-api:512.0MB", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("command", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TopProcessesCheck_ProducesBoundedInfoSnapshot()
    {
        var check = new TopProcessesCheck();
        var context = new DiagnosticContext(new ErpDoctorOptions
        {
            System = new SystemOptions { TopProcessesLimit = 3 }
        });

        var result = await check.ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(DiagnosticStatus.Info, result.Status);
        Assert.Equal("3", result.EvidenceOrEmpty["topProcessLimit"]);
        Assert.True(result.EvidenceOrEmpty.ContainsKey("topProcesses"));
        Assert.False(result.EvidenceOrEmpty.ContainsKey("commandLine"));
        Assert.False(result.EvidenceOrEmpty.ContainsKey("environment"));
    }
}
