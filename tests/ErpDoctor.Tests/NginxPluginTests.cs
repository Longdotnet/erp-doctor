using System.Text.Json;
using ErpDoctor.Core;
using ErpDoctor.Plugin.Nginx;
using ErpDoctor.PluginHost;
using ErpDoctor.PluginSdk;
using Xunit;
using NginxPluginType = ErpDoctor.Plugin.Nginx.NginxPlugin;

namespace ErpDoctor.Tests;

public sealed class NginxPluginTests
{
    [Fact]
    public void Settings_AreBoundedAndCriticalThresholdCannotFallBelowWarning()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "nginxExecutable": "  /usr/sbin/nginx  ",
              "configPath": "  /etc/nginx/nginx.conf  ",
              "commandTimeoutSeconds": 999,
              "loadPerCpuWarning": 3.5,
              "loadPerCpuCritical": 1.0
            }
            """);

        var settings = NginxSettings.From(document.RootElement);

        Assert.Equal("/usr/sbin/nginx", settings.Executable);
        Assert.Equal("/etc/nginx/nginx.conf", settings.ConfigPath);
        Assert.Equal(60, settings.CommandTimeoutSeconds);
        Assert.Equal(3.5d, settings.LoadPerCpuWarning);
        Assert.Equal(3.5d, settings.LoadPerCpuCritical);
    }

    [Fact]
    public void Plugin_RegistersLinuxRuntimeVersionAndConfigChecks()
    {
        var plugin = new NginxPluginType();

        var checks = plugin.CreateChecks(
            new PluginContext(null, Environment.CurrentDirectory));

        Assert.Equal(3, checks.Count);
        Assert.Contains(checks, check => check.Id == "linux-runtime" && check.Category == "linux");
        Assert.Contains(checks, check => check.Id == "version" && check.Category == "nginx");
        Assert.Contains(checks, check => check.Id == "config" && check.Category == "nginx");
    }

    [Fact]
    public void PluginHost_DiscoversAndNamespacesNginxChecks()
    {
        var options = new PluginOptions
        {
            Assemblies = [typeof(NginxPluginType).Assembly.Location]
        };

        var discovery = new PluginLoader().Load(options, Environment.CurrentDirectory);

        var plugin = Assert.Single(discovery.Plugins);
        Assert.Empty(discovery.Issues);
        Assert.Equal("nginx", plugin.Id);
        Assert.Equal(3, plugin.Checks.Count);
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.nginx.linux-runtime");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.nginx.version");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.nginx.config");
        Assert.All(plugin.Checks, check => Assert.Equal("plugin", check.Category));
    }

    [Fact]
    public void LinuxSnapshotParser_ReadsDistributionLoadUptimeAndAvailableMemory()
    {
        const string osRelease = """
            NAME="Ubuntu"
            PRETTY_NAME="Ubuntu 24.04 LTS"
            VERSION_ID="24.04"
            """;
        const string uptime = "7200.00 1000.00";
        const string loadavg = "2.00 1.50 1.00 1/100 123";
        const string meminfo = """
            MemTotal:        8000000 kB
            MemAvailable:    4000000 kB
            """;

        var snapshot = LinuxSnapshotParser.Parse(
            osRelease,
            uptime,
            loadavg,
            meminfo,
            processorCount: 4);

        Assert.Equal("Ubuntu 24.04 LTS", snapshot.Distribution);
        Assert.Equal("24.04", snapshot.Version);
        Assert.Equal(2d, snapshot.UptimeHours, 6);
        Assert.Equal(2d, snapshot.Load1, 6);
        Assert.Equal(1.5d, snapshot.Load5, 6);
        Assert.Equal(1d, snapshot.Load15, 6);
        Assert.Equal(4, snapshot.ProcessorCount);
        Assert.True(snapshot.MemoryAvailablePercent.HasValue);
        Assert.Equal(50d, snapshot.MemoryAvailablePercent.GetValueOrDefault(), 6);
    }

    [Fact]
    public void LinuxSnapshotEvaluator_UsesLoadPerCpuThresholds()
    {
        var settings = new NginxSettings(
            "nginx",
            null,
            10,
            LoadPerCpuWarning: 1d,
            LoadPerCpuCritical: 2d);

        var healthy = LinuxSnapshotEvaluator.Evaluate(
            Snapshot(load1: 2d, cpuCount: 4),
            settings);
        var warning = LinuxSnapshotEvaluator.Evaluate(
            Snapshot(load1: 4d, cpuCount: 4),
            settings);
        var critical = LinuxSnapshotEvaluator.Evaluate(
            Snapshot(load1: 8d, cpuCount: 4),
            settings);

        Assert.Equal(PluginDiagnosticStatus.Healthy, healthy.Status);
        Assert.Equal(PluginDiagnosticStatus.Warning, warning.Status);
        Assert.Equal(PluginDiagnosticStatus.Critical, critical.Status);
        Assert.Equal("2.00", critical.EvidenceOrEmpty["load1PerCpu"]);
    }

    [Fact]
    public void NginxVersionEvaluator_ParsesStandardVersionOutput()
    {
        var result = new NginxCommandResult(
            true,
            false,
            0,
            string.Empty,
            "nginx version: nginx/1.26.2",
            string.Empty);

        var diagnostic = NginxVersionEvaluator.Evaluate(result);

        Assert.Equal(PluginDiagnosticStatus.Healthy, diagnostic.Status);
        Assert.Equal("1.26.2", diagnostic.EvidenceOrEmpty["nginxVersion"]);
        Assert.Contains("1.26.2", diagnostic.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NginxConfigEvaluator_FailureDoesNotExposeRawStderr()
    {
        const string rawSecret = "password=must-not-appear";
        var result = new NginxCommandResult(
            false,
            false,
            1,
            string.Empty,
            $"nginx: [emerg] invalid directive {rawSecret}",
            "Nginx CLI exited with code 1.");

        var diagnostic = NginxConfigEvaluator.Evaluate(
            result,
            "/etc/nginx/nginx.conf");
        var rendered = string.Join("\n", diagnostic.EvidenceOrEmpty.Values);

        Assert.Equal(PluginDiagnosticStatus.Critical, diagnostic.Status);
        Assert.DoesNotContain(rawSecret, diagnostic.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(rawSecret, rendered, StringComparison.Ordinal);
        Assert.Equal("/etc/nginx/nginx.conf", diagnostic.EvidenceOrEmpty["configPath"]);
        Assert.Equal("1", diagnostic.EvidenceOrEmpty["exitCode"]);
    }

    [Fact]
    public async Task NginxChecks_AreSkippedOnNonLinuxHosts()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        var plugin = new NginxPluginType();
        var context = new PluginContext(null, Environment.CurrentDirectory);

        foreach (var check in plugin.CreateChecks(context))
        {
            var result = await check.ExecuteAsync(
                context,
                TestContext.Current.CancellationToken);
            Assert.Equal(PluginDiagnosticStatus.Skipped, result.Status);
        }
    }

    private static LinuxRuntimeSnapshot Snapshot(double load1, int cpuCount) =>
        new(
            "Ubuntu 24.04 LTS",
            "24.04",
            10,
            load1,
            load1,
            load1,
            cpuCount,
            50);
}
