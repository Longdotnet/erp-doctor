using System.Text.Json;
using ErpDoctor.Core;
using ErpDoctor.Plugin.Redis;
using ErpDoctor.PluginHost;
using ErpDoctor.PluginSdk;
using Xunit;
using RedisPluginType = ErpDoctor.Plugin.Redis.RedisPlugin;

namespace ErpDoctor.Tests;

public sealed class RedisPluginTests
{
    [Fact]
    public void Settings_AreBoundedAndCriticalThresholdsCannotFallBelowWarning()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "redisCliExecutable": "  redis-cli-custom  ",
              "host": "  redis.internal  ",
              "port": 70000,
              "username": "  doctor  ",
              "passwordEnvironmentVariable": "  ERP_REDIS_PASSWORD  ",
              "tls": true,
              "caCertificatePath": "  /etc/ssl/redis-ca.pem  ",
              "commandTimeoutSeconds": 999,
              "memoryWarningPercent": 85,
              "memoryCriticalPercent": 70,
              "replicaLagWarningSeconds": 15,
              "replicaLagCriticalSeconds": 5
            }
            """);

        var settings = RedisSettings.From(document.RootElement);

        Assert.Equal("redis-cli-custom", settings.Executable);
        Assert.Equal("redis.internal", settings.Host);
        Assert.Equal(65535, settings.Port);
        Assert.Equal("doctor", settings.Username);
        Assert.Equal("ERP_REDIS_PASSWORD", settings.PasswordEnvironmentVariable);
        Assert.True(settings.UseTls);
        Assert.Equal("/etc/ssl/redis-ca.pem", settings.CaCertificatePath);
        Assert.Equal(60, settings.CommandTimeoutSeconds);
        Assert.Equal(85d, settings.MemoryWarningPercent);
        Assert.Equal(85d, settings.MemoryCriticalPercent);
        Assert.Equal(15, settings.ReplicaLagWarningSeconds);
        Assert.Equal(15, settings.ReplicaLagCriticalSeconds);
    }

    [Fact]
    public void Plugin_RegistersFiveReadOnlyChecks()
    {
        var plugin = new RedisPluginType();

        var checks = plugin.CreateChecks(
            new PluginContext(null, Environment.CurrentDirectory));

        Assert.Equal(5, checks.Count);
        Assert.Contains(checks, check => check.Id == "connectivity");
        Assert.Contains(checks, check => check.Id == "server");
        Assert.Contains(checks, check => check.Id == "memory");
        Assert.Contains(checks, check => check.Id == "persistence");
        Assert.Contains(checks, check => check.Id == "replication");
        Assert.All(checks, check => Assert.Equal("redis", check.Category));
    }

    [Fact]
    public void PluginHost_DiscoversAndNamespacesRedisChecks()
    {
        var options = new PluginOptions
        {
            Assemblies = [typeof(RedisPluginType).Assembly.Location]
        };

        var discovery = new PluginLoader().Load(options, Environment.CurrentDirectory);

        var plugin = Assert.Single(discovery.Plugins);
        Assert.Empty(discovery.Issues);
        Assert.Equal("redis", plugin.Id);
        Assert.Equal(5, plugin.Checks.Count);
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.redis.connectivity");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.redis.server");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.redis.memory");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.redis.persistence");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.redis.replication");
    }

    [Fact]
    public void InfoParser_IgnoresHeadersAndSplitsOnlyOnFirstColon()
    {
        const string output = """
            # Server
            redis_version:8.2.1
            config_file:C:\redis\redis.conf

            # Memory
            used_memory:1048576
            malformed-line
            """;

        var parsed = RedisInfoParser.Parse(output);

        Assert.Equal("8.2.1", parsed["redis_version"]);
        Assert.Equal("C:\\redis\\redis.conf", parsed["config_file"]);
        Assert.Equal("1048576", parsed["used_memory"]);
        Assert.False(parsed.ContainsKey("malformed-line"));
    }

    [Fact]
    public void PingEvaluator_RequiresPong()
    {
        var healthy = RedisPingEvaluator.Evaluate("PONG");
        var unexpected = RedisPingEvaluator.Evaluate("unexpected");

        Assert.Equal(PluginDiagnosticStatus.Healthy, healthy.Status);
        Assert.Equal(PluginDiagnosticStatus.Error, unexpected.Status);
    }

    [Fact]
    public void ServerEvaluator_EmitsOnlyBoundedMetadata()
    {
        var info = RedisInfoParser.Parse(
            """
            redis_version:8.2.1
            redis_mode:standalone
            uptime_in_seconds:7200
            run_id:secret-like-runtime-identifier
            config_file:/etc/redis/redis.conf
            executable:/usr/bin/redis-server
            """);

        var result = RedisServerEvaluator.Evaluate(info);

        Assert.Equal(PluginDiagnosticStatus.Healthy, result.Status);
        Assert.Equal("8.2.1", result.EvidenceOrEmpty["redisVersion"]);
        Assert.Equal("standalone", result.EvidenceOrEmpty["redisMode"]);
        Assert.Equal("2.00", result.EvidenceOrEmpty["uptimeHours"]);
        Assert.False(result.EvidenceOrEmpty.ContainsKey("run_id"));
        Assert.False(result.EvidenceOrEmpty.ContainsKey("config_file"));
        Assert.False(result.EvidenceOrEmpty.ContainsKey("executable"));
    }

    [Fact]
    public void MemoryEvaluator_UsesConfiguredMaxMemoryThresholds()
    {
        var info = RedisInfoParser.Parse(
            """
            used_memory:94371840
            used_memory_peak:104857600
            maxmemory:104857600
            mem_fragmentation_ratio:1.25
            """);
        var settings = Settings(memoryWarning: 80, memoryCritical: 90);

        var result = RedisMemoryEvaluator.Evaluate(info, settings);

        Assert.Equal(PluginDiagnosticStatus.Critical, result.Status);
        Assert.Equal("90.00", result.EvidenceOrEmpty["usedMemoryPercent"]);
        Assert.Equal("1.25", result.EvidenceOrEmpty["fragmentationRatio"]);
    }

    [Fact]
    public void MemoryEvaluator_NoMaxMemoryIsInformational()
    {
        var info = RedisInfoParser.Parse(
            """
            used_memory:10485760
            maxmemory:0
            """);

        var result = RedisMemoryEvaluator.Evaluate(info, Settings());

        Assert.Equal(PluginDiagnosticStatus.Info, result.Status);
        Assert.Equal("unlimited", result.EvidenceOrEmpty["maxMemoryMb"]);
    }

    [Fact]
    public void PersistenceEvaluator_LastBackgroundSaveFailureIsCritical()
    {
        var info = RedisInfoParser.Parse(
            """
            loading:0
            rdb_last_bgsave_status:err
            aof_enabled:1
            aof_last_bgrewrite_status:ok
            """);

        var result = RedisPersistenceEvaluator.Evaluate(info);

        Assert.Equal(PluginDiagnosticStatus.Critical, result.Status);
        Assert.Equal("err", result.EvidenceOrEmpty["rdbLastBgsaveStatus"]);
    }

    [Fact]
    public void PersistenceEvaluator_LoadingIsWarning()
    {
        var info = RedisInfoParser.Parse(
            """
            loading:1
            rdb_last_bgsave_status:ok
            aof_enabled:0
            """);

        var result = RedisPersistenceEvaluator.Evaluate(info);

        Assert.Equal(PluginDiagnosticStatus.Warning, result.Status);
        Assert.Equal("true", result.EvidenceOrEmpty["loading"]);
    }

    [Fact]
    public void ReplicationEvaluator_PrimaryIsHealthyWithoutReplicaDetails()
    {
        var info = RedisInfoParser.Parse(
            """
            role:master
            connected_slaves:2
            slave0:ip=10.0.0.10,port=6379,state=online,offset=1,lag=0
            """);

        var result = RedisReplicationEvaluator.Evaluate(info, Settings());

        Assert.Equal(PluginDiagnosticStatus.Healthy, result.Status);
        Assert.Equal("2", result.EvidenceOrEmpty["connectedReplicas"]);
        Assert.False(result.EvidenceOrEmpty.ContainsKey("slave0"));
    }

    [Fact]
    public void ReplicationEvaluator_DownPrimaryLinkIsCritical()
    {
        var info = RedisInfoParser.Parse(
            """
            role:slave
            master_link_status:down
            master_last_io_seconds_ago:120
            master_sync_in_progress:0
            """);

        var result = RedisReplicationEvaluator.Evaluate(info, Settings());

        Assert.Equal(PluginDiagnosticStatus.Critical, result.Status);
        Assert.Equal("down", result.EvidenceOrEmpty["primaryLinkStatus"]);
    }

    [Fact]
    public void ReplicationEvaluator_UsesLagThresholds()
    {
        var info = RedisInfoParser.Parse(
            """
            role:slave
            master_link_status:up
            master_last_io_seconds_ago:12
            master_sync_in_progress:0
            """);

        var result = RedisReplicationEvaluator.Evaluate(
            info,
            Settings(lagWarning: 10, lagCritical: 30));

        Assert.Equal(PluginDiagnosticStatus.Warning, result.Status);
        Assert.Equal("12", result.EvidenceOrEmpty["lastPrimaryIoSecondsAgo"]);
    }

    [Fact]
    public async Task MissingPasswordEnvironmentVariableFailsBeforeStartingCli()
    {
        var variableName = $"ERP_DOCTOR_REDIS_MISSING_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, null);
        using var document = JsonDocument.Parse(
            $$"""
            {
              "redisCliExecutable": "definitely-not-needed-for-this-test",
              "passwordEnvironmentVariable": "{{variableName}}"
            }
            """);
        var plugin = new RedisPluginType();
        var context = new PluginContext(document.RootElement.Clone(), Environment.CurrentDirectory);
        var check = plugin.CreateChecks(context).Single(item => item.Id == "connectivity");

        var result = await check.ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(PluginDiagnosticStatus.Error, result.Status);
        Assert.Contains(variableName, result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("password=", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingRedisCliReturnsErrorWithoutRawStderr()
    {
        var missingExecutable = Path.Combine(
            Path.GetTempPath(),
            $"erp-doctor-redis-missing-{Guid.NewGuid():N}");
        using var document = JsonDocument.Parse(
            $$"""
            {
              "redisCliExecutable": "{{missingExecutable.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "commandTimeoutSeconds": 1
            }
            """);
        var plugin = new RedisPluginType();
        var context = new PluginContext(document.RootElement.Clone(), Environment.CurrentDirectory);
        var check = plugin.CreateChecks(context).Single(item => item.Id == "connectivity");

        var result = await check.ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(PluginDiagnosticStatus.Error, result.Status);
        Assert.Contains("could not be started", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stderr", result.EvidenceOrEmpty.Keys, StringComparer.OrdinalIgnoreCase);
    }

    private static RedisSettings Settings(
        double memoryWarning = 80,
        double memoryCritical = 90,
        int lagWarning = 10,
        int lagCritical = 30) =>
        new(
            "redis-cli",
            "127.0.0.1",
            6379,
            null,
            null,
            false,
            null,
            10,
            memoryWarning,
            memoryCritical,
            lagWarning,
            lagCritical);
}
