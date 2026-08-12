using System.Text.Json;
using ErpDoctor.Core;
using ErpDoctor.Plugin.Docker;
using ErpDoctor.PluginHost;
using ErpDoctor.PluginSdk;
using Xunit;
using DockerPluginType = ErpDoctor.Plugin.Docker.DockerPlugin;

namespace ErpDoctor.Tests;

public sealed class DockerPluginTests
{
    [Fact]
    public void Settings_AreBoundedAndExpectedContainersAreDistinct()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "dockerExecutable": "  docker-custom  ",
              "commandTimeoutSeconds": 999,
              "warnOnStoppedContainers": true,
              "maxContainerEvidence": 0,
              "expectedContainers": ["erp-api", "ERP-API", "redis", ""]
            }
            """);

        var settings = DockerSettings.From(document.RootElement);

        Assert.Equal("docker-custom", settings.Executable);
        Assert.Equal(60, settings.CommandTimeoutSeconds);
        Assert.True(settings.WarnOnStoppedContainers);
        Assert.Equal(1, settings.MaxContainerEvidence);
        Assert.Equal(2, settings.ExpectedContainers.Count);
        Assert.Contains(settings.ExpectedContainers, value => value.Equals("erp-api", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(settings.ExpectedContainers, value => value.Equals("redis", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plugin_RegistersThreeReadOnlyChecks()
    {
        var plugin = new DockerPluginType();

        var checks = plugin.CreateChecks(
            new PluginContext(null, Environment.CurrentDirectory));

        Assert.Equal(3, checks.Count);
        Assert.Contains(checks, check => check.Id == "engine");
        Assert.Contains(checks, check => check.Id == "info");
        Assert.Contains(checks, check => check.Id == "containers");
        Assert.All(checks, check => Assert.Equal("docker", check.Category));
    }

    [Fact]
    public void PluginHost_DiscoversAndNamespacesDockerChecks()
    {
        var options = new PluginOptions
        {
            Assemblies = [typeof(DockerPluginType).Assembly.Location]
        };

        var discovery = new PluginLoader().Load(options, Environment.CurrentDirectory);

        var plugin = Assert.Single(discovery.Plugins);
        Assert.Empty(discovery.Issues);
        Assert.Equal("docker", plugin.Id);
        Assert.Equal(3, plugin.Checks.Count);
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.docker.engine");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.docker.info");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.docker.containers");
    }

    [Fact]
    public void EngineEvaluator_ReadsOnlyBoundedServerMetadata()
    {
        const string json = """
            {
              "Client": { "Version": "28.5.1" },
              "Server": {
                "Version": "28.5.1",
                "ApiVersion": "1.51",
                "Os": "linux",
                "Arch": "amd64"
              }
            }
            """;

        var result = DockerEngineEvaluator.Evaluate(json);

        Assert.Equal(PluginDiagnosticStatus.Healthy, result.Status);
        Assert.Equal("28.5.1", result.EvidenceOrEmpty["serverVersion"]);
        Assert.Equal("1.51", result.EvidenceOrEmpty["apiVersion"]);
        Assert.Equal("linux", result.EvidenceOrEmpty["os"]);
        Assert.Equal("amd64", result.EvidenceOrEmpty["architecture"]);
    }

    [Fact]
    public void InfoEvaluator_CountsWarningsWithoutIncludingWarningText()
    {
        const string secretLikeWarning = "proxy password=must-not-appear";
        var json = $$"""
            {
              "ServerVersion": "28.5.1",
              "Containers": 4,
              "ContainersRunning": 2,
              "ContainersPaused": 0,
              "ContainersStopped": 2,
              "Images": 8,
              "OSType": "linux",
              "Architecture": "amd64",
              "Warnings": ["{{secretLikeWarning}}"]
            }
            """;

        var result = DockerInfoEvaluator.Evaluate(json);
        var renderedEvidence = string.Join("\n", result.EvidenceOrEmpty.Values);

        Assert.Equal(PluginDiagnosticStatus.Warning, result.Status);
        Assert.Equal("1", result.EvidenceOrEmpty["warningCount"]);
        Assert.DoesNotContain(secretLikeWarning, result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(secretLikeWarning, renderedEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainerParserAndEvaluator_HealthyWhenExpectedContainersRun()
    {
        const string output = """
            {"Names":"erp-api","State":"running","Status":"Up 5 minutes (healthy)","HealthStatus":"healthy"}
            {"Names":"redis","State":"running","Status":"Up 10 minutes","HealthStatus":""}
            """;
        var settings = new DockerSettings(
            "docker",
            10,
            false,
            20,
            ["erp-api", "redis"]);

        var containers = DockerContainerParser.ParseLines(output);
        var result = DockerContainerEvaluator.Evaluate(containers, settings);

        Assert.Equal(PluginDiagnosticStatus.Healthy, result.Status);
        Assert.Equal("2", result.EvidenceOrEmpty["runningCount"]);
        Assert.Equal("0", result.EvidenceOrEmpty["missingExpectedCount"]);
    }

    [Fact]
    public void ContainerEvaluator_MissingExpectedContainerIsCritical()
    {
        var settings = new DockerSettings(
            "docker",
            10,
            false,
            20,
            ["erp-api", "redis"]);
        var containers = new[]
        {
            new DockerContainerSnapshot("erp-api", "running", "healthy")
        };

        var result = DockerContainerEvaluator.Evaluate(containers, settings);

        Assert.Equal(PluginDiagnosticStatus.Critical, result.Status);
        Assert.Equal("1", result.EvidenceOrEmpty["missingExpectedCount"]);
        Assert.Contains("redis", result.EvidenceOrEmpty["missingExpected"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContainerEvaluator_UnhealthyOrRestartingContainerIsCritical()
    {
        var settings = new DockerSettings(
            "docker",
            10,
            false,
            20,
            Array.Empty<string>());
        var containers = new[]
        {
            new DockerContainerSnapshot("api", "running", "unhealthy"),
            new DockerContainerSnapshot("worker", "restarting", string.Empty)
        };

        var result = DockerContainerEvaluator.Evaluate(containers, settings);

        Assert.Equal(PluginDiagnosticStatus.Critical, result.Status);
        Assert.Equal("1", result.EvidenceOrEmpty["unhealthyCount"]);
        Assert.Equal("1", result.EvidenceOrEmpty["severeStateCount"]);
        Assert.Contains(
            result.EvidenceOrEmpty.Values,
            value => value.Contains("name=api", StringComparison.Ordinal));
        Assert.Contains(
            result.EvidenceOrEmpty.Values,
            value => value.Contains("name=worker", StringComparison.Ordinal));
    }

    [Fact]
    public void ContainerEvaluator_StoppedContainerWarningIsOptIn()
    {
        var containers = new[]
        {
            new DockerContainerSnapshot("old-job", "exited", string.Empty)
        };
        var quietSettings = new DockerSettings(
            "docker",
            10,
            false,
            20,
            Array.Empty<string>());
        var noisySettings = quietSettings with { WarnOnStoppedContainers = true };

        var quiet = DockerContainerEvaluator.Evaluate(containers, quietSettings);
        var noisy = DockerContainerEvaluator.Evaluate(containers, noisySettings);

        Assert.Equal(PluginDiagnosticStatus.Healthy, quiet.Status);
        Assert.Equal(PluginDiagnosticStatus.Warning, noisy.Status);
    }

    [Fact]
    public async Task MissingDockerExecutableReturnsErrorWithoutRawStderr()
    {
        var missingExecutable = Path.Combine(
            Path.GetTempPath(),
            $"erp-doctor-docker-missing-{Guid.NewGuid():N}");
        using var document = JsonDocument.Parse(
            $$"""
            {
              "dockerExecutable": "{{missingExecutable.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "commandTimeoutSeconds": 1
            }
            """);
        var plugin = new DockerPluginType();
        var check = plugin.CreateChecks(
                new PluginContext(document.RootElement.Clone(), Environment.CurrentDirectory))
            .Single(item => item.Id == "engine");

        var result = await check.ExecuteAsync(
            new PluginContext(document.RootElement.Clone(), Environment.CurrentDirectory),
            TestContext.Current.CancellationToken);

        Assert.Equal(PluginDiagnosticStatus.Error, result.Status);
        Assert.Contains("could not be started", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stderr", result.EvidenceOrEmpty.Keys, StringComparer.OrdinalIgnoreCase);
    }
}
