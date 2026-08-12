using System.Text;
using System.Text.Json;
using ErpDoctor.Plugin.RabbitMq;
using ErpDoctor.PluginHost;
using ErpDoctor.PluginSdk;
using Xunit;
using RabbitMqPluginType = ErpDoctor.Plugin.RabbitMq.RabbitMqPlugin;

namespace ErpDoctor.Tests;

public sealed class RabbitMqPluginTests
{
    [Fact]
    public void Settings_AreBoundedAndCriticalThresholdsCannotFallBelowWarning()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "baseUrl": "  https://rabbit.internal:15672///  ",
              "username": "  doctor  ",
              "passwordEnvironmentVariable": "  ERP_RABBIT_PASSWORD  ",
              "virtualHost": "  /erp  ",
              "requestTimeoutSeconds": 999,
              "maxQueues": 9999,
              "readyMessagesWarning": 2000,
              "readyMessagesCritical": 1000,
              "unackedMessagesWarning": 800,
              "unackedMessagesCritical": 100,
              "warnOnNoConsumersWithReadyMessages": true,
              "maxQueueEvidence": 999
            }
            """);

        var settings = RabbitMqSettings.From(document.RootElement);

        Assert.Equal("https://rabbit.internal:15672", settings.BaseUrl);
        Assert.Equal("doctor", settings.Username);
        Assert.Equal("ERP_RABBIT_PASSWORD", settings.PasswordEnvironmentVariable);
        Assert.Equal("/erp", settings.VirtualHost);
        Assert.Equal(60, settings.RequestTimeoutSeconds);
        Assert.Equal(500, settings.MaxQueues);
        Assert.Equal(2000, settings.ReadyMessagesWarning);
        Assert.Equal(2000, settings.ReadyMessagesCritical);
        Assert.Equal(800, settings.UnackedMessagesWarning);
        Assert.Equal(800, settings.UnackedMessagesCritical);
        Assert.True(settings.WarnOnNoConsumersWithReadyMessages);
        Assert.Equal(50, settings.MaxQueueEvidence);
    }

    [Fact]
    public void Plugin_RegistersThreeReadOnlyChecks()
    {
        var plugin = new RabbitMqPluginType();
        var checks = plugin.CreateChecks(
            new PluginContext(null, Environment.CurrentDirectory));

        Assert.Equal(3, checks.Count);
        Assert.Contains(checks, check => check.Id == "overview");
        Assert.Contains(checks, check => check.Id == "nodes");
        Assert.Contains(checks, check => check.Id == "queues");
        Assert.All(checks, check => Assert.Equal("rabbitmq", check.Category));
    }

    [Fact]
    public void PluginHost_DiscoversAndNamespacesRabbitMqChecks()
    {
        var options = new PluginOptions
        {
            Assemblies = [typeof(RabbitMqPluginType).Assembly.Location]
        };

        var discovery = new PluginLoader().Load(options, Environment.CurrentDirectory);

        var plugin = Assert.Single(discovery.Plugins);
        Assert.Empty(discovery.Issues);
        Assert.Equal("rabbitmq", plugin.Id);
        Assert.Equal(3, plugin.Checks.Count);
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.rabbitmq.overview");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.rabbitmq.nodes");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.rabbitmq.queues");
    }

    [Fact]
    public void QueuePath_UsesBoundedPagination()
    {
        var path = RabbitMqApiClient.QueuePath(Settings(maxQueues: 250));

        Assert.Equal("api/queues?page=1&page_size=250&pagination=true", path);
    }

    [Fact]
    public void QueuePath_EncodesVirtualHost()
    {
        var path = RabbitMqApiClient.QueuePath(Settings(virtualHost: "/"));

        Assert.Equal("api/queues/%2F?page=1&page_size=100&pagination=true", path);
    }

    [Fact]
    public void BasicAuthHelper_ProducesOnlyStandardCredentialPayload()
    {
        var encoded = RabbitMqApiClient.CreateBasicAuthParameter("doctor", "secret-value");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

        Assert.Equal("doctor:secret-value", decoded);
    }

    [Fact]
    public void OverviewEvaluator_EmitsOnlyWhitelistedOperationalEvidence()
    {
        const string json = """
            {
              "rabbitmq_version": "4.1.3",
              "management_version": "4.1.3",
              "cluster_name": "erp-broker",
              "object_totals": {
                "connections": 8,
                "channels": 12,
                "exchanges": 22,
                "queues": 14,
                "consumers": 19
              },
              "queue_totals": {
                "messages": 300,
                "messages_ready": 120,
                "messages_unacknowledged": 180
              },
              "contexts": [{"password": "must-not-appear"}],
              "listeners": [{"ip_address": "10.0.0.9"}],
              "auth_mechanisms": ["PLAIN"]
            }
            """;

        var result = RabbitMqOverviewEvaluator.Evaluate(json);

        Assert.Equal(PluginDiagnosticStatus.Healthy, result.Status);
        Assert.Equal("4.1.3", result.EvidenceOrEmpty["rabbitMqVersion"]);
        Assert.Equal("erp-broker", result.EvidenceOrEmpty["clusterName"]);
        Assert.Equal("14", result.EvidenceOrEmpty["queues"]);
        Assert.Equal("120", result.EvidenceOrEmpty["messagesReady"]);
        var rendered = string.Join("\n", result.EvidenceOrEmpty.Select(pair => $"{pair.Key}={pair.Value}"));
        Assert.DoesNotContain("must-not-appear", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.9", rendered, StringComparison.Ordinal);
        Assert.False(result.EvidenceOrEmpty.ContainsKey("contexts"));
        Assert.False(result.EvidenceOrEmpty.ContainsKey("listeners"));
    }

    [Fact]
    public void NodeEvaluator_HealthyWhenNodesRunWithoutAlarmsOrPartitions()
    {
        const string json = """
            [
              {
                "name": "rabbit@node1",
                "running": true,
                "mem_alarm": false,
                "disk_free_alarm": false,
                "partitions": [],
                "config_files": ["/etc/rabbitmq/rabbitmq.conf"]
              }
            ]
            """;

        var result = RabbitMqNodeEvaluator.Evaluate(json);

        Assert.Equal(PluginDiagnosticStatus.Healthy, result.Status);
        Assert.Equal("1", result.EvidenceOrEmpty["runningNodes"]);
        Assert.False(result.EvidenceOrEmpty.ContainsKey("config_files"));
    }

    [Theory]
    [InlineData("false", "false", "[]")]
    [InlineData("true", "false", "[]")]
    [InlineData("false", "true", "[]")]
    [InlineData("false", "false", "[\"rabbit@node2\"]")]
    public void NodeEvaluator_CriticalForBrokerNativeAlarmsOrPartitions(
        string memAlarm,
        string diskAlarm,
        string partitions)
    {
        var json = $$"""
            [
              {
                "name": "rabbit@node1",
                "running": true,
                "mem_alarm": {{memAlarm}},
                "disk_free_alarm": {{diskAlarm}},
                "partitions": {{partitions}}
              }
            ]
            """;

        var result = RabbitMqNodeEvaluator.Evaluate(json);

        var expectedCritical = memAlarm == "true" || diskAlarm == "true" || partitions != "[]";
        Assert.Equal(
            expectedCritical ? PluginDiagnosticStatus.Critical : PluginDiagnosticStatus.Healthy,
            result.Status);
    }

    [Fact]
    public void NodeEvaluator_DownNodeIsCriticalAndEvidenceIsBounded()
    {
        const string json = """
            [
              {
                "name": "rabbit@node1",
                "running": false,
                "mem_alarm": false,
                "disk_free_alarm": false,
                "partitions": [],
                "log_files": ["/var/log/rabbitmq/secret.log"]
              }
            ]
            """;

        var result = RabbitMqNodeEvaluator.Evaluate(json);

        Assert.Equal(PluginDiagnosticStatus.Critical, result.Status);
        Assert.Equal("1", result.EvidenceOrEmpty["downNodes"]);
        Assert.DoesNotContain("secret.log", string.Join("\n", result.EvidenceOrEmpty.Values), StringComparison.Ordinal);
    }

    [Fact]
    public void QueueEvaluator_PaginatedResponseDetectsCriticalAndWarningBacklog()
    {
        const string json = """
            {
              "total_count": 350,
              "items": [
                {
                  "name": "orders",
                  "vhost": "/erp",
                  "messages_ready": 12000,
                  "messages_unacknowledged": 10,
                  "consumers": 3,
                  "arguments": {"x-secret": "must-not-appear"}
                },
                {
                  "name": "billing",
                  "vhost": "/erp",
                  "messages_ready": 50,
                  "messages_unacknowledged": 900,
                  "consumers": 2
                }
              ]
            }
            """;

        var result = RabbitMqQueueEvaluator.Evaluate(json, Settings());

        Assert.Equal(PluginDiagnosticStatus.Critical, result.Status);
        Assert.Equal("350", result.EvidenceOrEmpty["totalQueues"]);
        Assert.Equal("true", result.EvidenceOrEmpty["scanTruncated"]);
        Assert.Equal("1", result.EvidenceOrEmpty["criticalQueues"]);
        Assert.Equal("1", result.EvidenceOrEmpty["warningQueues"]);
        Assert.Contains("/erp/orders", result.EvidenceOrEmpty["queueIssues"], StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-appear", result.EvidenceOrEmpty["queueIssues"], StringComparison.Ordinal);
    }

    [Fact]
    public void QueueEvaluator_NoConsumerWarningIsOptIn()
    {
        const string json = """
            [
              {
                "name": "offline-processing",
                "vhost": "/",
                "messages_ready": 5,
                "messages_unacknowledged": 0,
                "consumers": 0
              }
            ]
            """;

        var defaultResult = RabbitMqQueueEvaluator.Evaluate(
            json,
            Settings(warnOnNoConsumers: false));
        var optInResult = RabbitMqQueueEvaluator.Evaluate(
            json,
            Settings(warnOnNoConsumers: true));

        Assert.Equal(PluginDiagnosticStatus.Healthy, defaultResult.Status);
        Assert.Equal(PluginDiagnosticStatus.Warning, optInResult.Status);
    }

    [Fact]
    public void QueueEvaluator_SupportsLegacyArrayResponse()
    {
        const string json = """
            [
              {
                "name": "healthy",
                "vhost": "/",
                "messages_ready": 0,
                "messages_unacknowledged": 0,
                "consumers": 1
              }
            ]
            """;

        var result = RabbitMqQueueEvaluator.Evaluate(json, Settings());

        Assert.Equal(PluginDiagnosticStatus.Healthy, result.Status);
        Assert.Equal("1", result.EvidenceOrEmpty["inspectedQueues"]);
        Assert.False(result.EvidenceOrEmpty.ContainsKey("totalQueues"));
    }

    [Fact]
    public void Evaluators_InvalidJsonReturnsError()
    {
        Assert.Equal(PluginDiagnosticStatus.Error, RabbitMqOverviewEvaluator.Evaluate("{").Status);
        Assert.Equal(PluginDiagnosticStatus.Error, RabbitMqNodeEvaluator.Evaluate("{").Status);
        Assert.Equal(PluginDiagnosticStatus.Error, RabbitMqQueueEvaluator.Evaluate("{", Settings()).Status);
    }

    [Fact]
    public async Task MissingPasswordEnvironmentVariableFailsBeforeNetwork()
    {
        var variableName = $"ERP_DOCTOR_RABBIT_MISSING_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, null);
        using var document = JsonDocument.Parse(
            $$"""
            {
              "baseUrl": "http://127.0.0.1:1",
              "username": "doctor",
              "passwordEnvironmentVariable": "{{variableName}}",
              "requestTimeoutSeconds": 1
            }
            """);
        var plugin = new RabbitMqPluginType();
        var context = new PluginContext(document.RootElement.Clone(), Environment.CurrentDirectory);
        var check = plugin.CreateChecks(context).Single(item => item.Id == "overview");

        var result = await check.ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(PluginDiagnosticStatus.Error, result.Status);
        Assert.Contains(variableName, result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", string.Join("\n", result.EvidenceOrEmpty.Values), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidBaseUrlFailsWithoutAttemptingNetwork()
    {
        var variableName = $"ERP_DOCTOR_RABBIT_SECRET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "test-secret");
        try
        {
            using var document = JsonDocument.Parse(
                $$"""
                {
                  "baseUrl": "ftp://rabbit.internal",
                  "username": "doctor",
                  "passwordEnvironmentVariable": "{{variableName}}"
                }
                """);
            var plugin = new RabbitMqPluginType();
            var context = new PluginContext(document.RootElement.Clone(), Environment.CurrentDirectory);
            var check = plugin.CreateChecks(context).Single(item => item.Id == "overview");

            var result = await check.ExecuteAsync(
                context,
                TestContext.Current.CancellationToken);

            Assert.Equal(PluginDiagnosticStatus.Error, result.Status);
            Assert.Contains("http/https", result.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("test-secret", result.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("test-secret", string.Join("\n", result.EvidenceOrEmpty.Values), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    private static RabbitMqSettings Settings(
        string? virtualHost = null,
        int maxQueues = 100,
        bool warnOnNoConsumers = false) =>
        new(
            "http://127.0.0.1:15672",
            "doctor",
            "ERP_DOCTOR_RABBITMQ_PASSWORD",
            virtualHost,
            10,
            maxQueues,
            1_000,
            10_000,
            500,
            5_000,
            warnOnNoConsumers,
            10);
}
