using System.Globalization;
using ErpDoctor.PluginSdk;

namespace ErpDoctor.Plugin.Redis;

public sealed class RedisPlugin : IErpDoctorPlugin
{
    public string Id => "redis";
    public string Name => "Redis Diagnostics";
    public string Version => "0.1.0";

    public IReadOnlyList<IPluginCheck> CreateChecks(PluginContext context)
    {
        var settings = RedisSettings.From(context.Configuration);
        return
        [
            new RedisConnectivityCheck(settings),
            new RedisServerCheck(settings),
            new RedisMemoryCheck(settings),
            new RedisPersistenceCheck(settings),
            new RedisReplicationCheck(settings)
        ];
    }
}

internal abstract class RedisCheckBase(RedisSettings settings) : IPluginCheck
{
    private readonly RedisCli _cli = new();

    protected RedisSettings Settings { get; } = settings;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public string Category => "redis";

    public abstract Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken);

    protected Task<RedisCliResult> RunAsync(
        IReadOnlyList<string> commandArguments,
        CancellationToken cancellationToken) =>
        _cli.RunAsync(Settings, commandArguments, cancellationToken);

    protected PluginDiagnosticResult CliFailure(RedisCliResult result) =>
        new(
            PluginDiagnosticStatus.Error,
            result.FailureSummary,
            new Dictionary<string, string>
            {
                ["redisCliExecutable"] = Settings.Executable,
                ["host"] = Settings.Host,
                ["port"] = Settings.Port.ToString(CultureInfo.InvariantCulture),
                ["tls"] = Settings.UseTls ? "true" : "false",
                ["timedOut"] = result.TimedOut ? "true" : "false",
                ["exitCode"] = result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a"
            },
            [
                "Confirm redis-cli is installed and the target host/port/TLS settings are correct.",
                "If Redis ACLs are enabled, grant only the read-only diagnostic commands required by this provider.",
                "ERP Doctor intentionally does not include raw redis-cli stderr or authentication material in evidence."
            ]);

    protected async Task<PluginDiagnosticResult> EvaluateInfoAsync(
        string section,
        Func<IReadOnlyDictionary<string, string>, PluginDiagnosticResult> evaluator,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(["INFO", section], cancellationToken);
        if (!result.Succeeded)
        {
            return CliFailure(result);
        }

        var parsed = RedisInfoParser.Parse(result.Stdout);
        return evaluator(parsed);
    }
}

internal sealed class RedisConnectivityCheck(RedisSettings settings) : RedisCheckBase(settings)
{
    public override string Id => "connectivity";
    public override string Name => "Connectivity";

    public override async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var result = await RunAsync(["PING"], cancellationToken);
        return result.Succeeded
            ? RedisPingEvaluator.Evaluate(result.Stdout)
            : CliFailure(result);
    }
}

internal sealed class RedisServerCheck(RedisSettings settings) : RedisCheckBase(settings)
{
    public override string Id => "server";
    public override string Name => "Server metadata";

    public override Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        return EvaluateInfoAsync("server", RedisServerEvaluator.Evaluate, cancellationToken);
    }
}

internal sealed class RedisMemoryCheck(RedisSettings settings) : RedisCheckBase(settings)
{
    public override string Id => "memory";
    public override string Name => "Memory pressure";

    public override Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        return EvaluateInfoAsync(
            "memory",
            info => RedisMemoryEvaluator.Evaluate(info, Settings),
            cancellationToken);
    }
}

internal sealed class RedisPersistenceCheck(RedisSettings settings) : RedisCheckBase(settings)
{
    public override string Id => "persistence";
    public override string Name => "Persistence state";

    public override Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        return EvaluateInfoAsync(
            "persistence",
            RedisPersistenceEvaluator.Evaluate,
            cancellationToken);
    }
}

internal sealed class RedisReplicationCheck(RedisSettings settings) : RedisCheckBase(settings)
{
    public override string Id => "replication";
    public override string Name => "Replication health";

    public override Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        return EvaluateInfoAsync(
            "replication",
            info => RedisReplicationEvaluator.Evaluate(info, Settings),
            cancellationToken);
    }
}
