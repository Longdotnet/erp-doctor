using System.Text.Json;
using ErpDoctor.Core;
using ErpDoctor.PluginHost;
using ErpDoctor.SamplePlugin;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class PluginLoaderTests
{
    [Fact]
    public async Task Load_DiscoversSamplePluginAndNamespacesCheck()
    {
        var options = new PluginOptions
        {
            Assemblies = [typeof(SamplePlugin).Assembly.Location]
        };

        var discovery = new PluginLoader().Load(options, Environment.CurrentDirectory);

        var plugin = Assert.Single(discovery.Plugins);
        Assert.Empty(discovery.Issues);
        Assert.Equal("sample", plugin.Id);

        var check = Assert.Single(plugin.Checks);
        Assert.Equal("plugin.sample.required-env", check.Id);
        Assert.Equal("plugin", check.Category);

        var result = await check.ExecuteAsync(
            new DiagnosticContext(new ErpDoctorOptions()),
            TestContext.Current.CancellationToken);

        Assert.Equal("sample", result.EvidenceOrEmpty["pluginId"]);
        Assert.Equal("0.1.0", result.EvidenceOrEmpty["pluginVersion"]);
        Assert.Equal("configuration", result.EvidenceOrEmpty["pluginCategory"]);
    }

    [Fact]
    public async Task Load_MissingAssemblyBecomesErrorDiagnostic()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            $"erp-doctor-missing-{Guid.NewGuid():N}.dll");
        var options = new PluginOptions
        {
            Assemblies = [missing]
        };

        var discovery = new PluginLoader().Load(options, Environment.CurrentDirectory);

        Assert.Empty(discovery.Plugins);
        Assert.Single(discovery.Issues);

        var check = Assert.Single(discovery.DiagnosticChecks);
        var result = await check.ExecuteAsync(
            new DiagnosticContext(new ErpDoctorOptions()),
            TestContext.Current.CancellationToken);

        Assert.Equal(DiagnosticStatus.Error, result.Status);
        Assert.Contains("does not exist", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_RejectsUrlsInsteadOfDownloadingPlugins()
    {
        var options = new PluginOptions
        {
            Assemblies = ["https://example.test/ErpDoctor.Plugin.Bad.dll"]
        };

        var discovery = new PluginLoader().Load(options, Environment.CurrentDirectory);

        Assert.Empty(discovery.Plugins);
        var issue = Assert.Single(discovery.Issues);
        Assert.Contains("local filesystem", issue.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SamplePlugin_DoesNotExposeEnvironmentVariableValue()
    {
        const string variableName = "ERP_DOCTOR_PLUGIN_TEST_SECRET";
        const string secretValue = "must-not-appear-in-diagnostic";
        var previous = Environment.GetEnvironmentVariable(variableName);

        try
        {
            Environment.SetEnvironmentVariable(variableName, secretValue);
            using var document = JsonDocument.Parse(
                $$"""
                {
                  "requiredEnvironmentVariable": "{{variableName}}"
                }
                """);

            var options = new PluginOptions
            {
                Assemblies = [typeof(SamplePlugin).Assembly.Location],
                Settings = new Dictionary<string, JsonElement>
                {
                    ["SAMPLE"] = document.RootElement.Clone()
                }
            };

            var discovery = new PluginLoader().Load(options, Environment.CurrentDirectory);
            var check = Assert.Single(Assert.Single(discovery.Plugins).Checks);
            var result = await check.ExecuteAsync(
                new DiagnosticContext(new ErpDoctorOptions()),
                TestContext.Current.CancellationToken);

            Assert.Equal(DiagnosticStatus.Healthy, result.Status);
            Assert.Contains(variableName, result.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(secretValue, result.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(
                secretValue,
                string.Join("\n", result.EvidenceOrEmpty.Values),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
        }
    }
}
