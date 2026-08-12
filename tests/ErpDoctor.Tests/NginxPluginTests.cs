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
    public void Settings_AreBoundedAndTrimmed()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "nginxExecutable": "  /usr/sbin/nginx  ",
              "configPath": "  /etc/nginx/nginx.conf  ",
              "commandTimeoutSeconds": 999
            }
            """);

        var settings = NginxSettings.From(document.RootElement);

        Assert.Equal("/usr/sbin/nginx", settings.Executable);
        Assert.Equal("/etc/nginx/nginx.conf", settings.ConfigPath);
        Assert.Equal(60, settings.CommandTimeoutSeconds);
    }

    [Fact]
    public void Plugin_RegistersVersionAndConfigChecksOnly()
    {
        var plugin = new NginxPluginType();

        var checks = plugin.CreateChecks(
            new PluginContext(null, Environment.CurrentDirectory));

        Assert.Equal(2, checks.Count);
        Assert.Contains(checks, check => check.Id == "version" && check.Category == "nginx");
        Assert.Contains(checks, check => check.Id == "config" && check.Category == "nginx");
        Assert.DoesNotContain(checks, check => check.Id == "linux-runtime");
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
        Assert.Equal(2, plugin.Checks.Count);
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.nginx.version");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.nginx.config");
        Assert.DoesNotContain(plugin.Checks, check => check.Id == "plugin.nginx.linux-runtime");
        Assert.All(plugin.Checks, check => Assert.Equal("plugin", check.Category));
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
}
