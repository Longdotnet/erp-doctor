using System.Globalization;
using ErpDoctor.PluginSdk;

namespace ErpDoctor.Plugin.RabbitMq;

public sealed class RabbitMqPlugin : IErpDoctorPlugin
{
    public string Id => "rabbitmq";
    public string Name => "RabbitMQ Diagnostics";
    public string Version => "0.1.0";

    public IReadOnlyList<IPluginCheck> CreateChecks(PluginContext context)
    {
        var settings = RabbitMqSettings.From(context.Configuration);
        return
        [
            new RabbitMqOverviewCheck(settings),
            new RabbitMqNodesCheck(settings),
            new RabbitMqQueuesCheck(settings)
        ];
    }
}

internal abstract class RabbitMqCheckBase(RabbitMqSettings settings) : IPluginCheck
{
    private readonly RabbitMqApiClient _client = new();

    protected RabbitMqSettings Settings { get; } = settings;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public string Category => "rabbitmq";

    public abstract Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken);

    protected Task<RabbitMqApiResult> GetAsync(
        string relativePath,
        CancellationToken cancellationToken) =>
        _client.GetAsync(Settings, relativePath, cancellationToken);

    protected PluginDiagnosticResult ApiFailure(RabbitMqApiResult result) =>
        new(
            PluginDiagnosticStatus.Error,
            result.FailureSummary,
            new Dictionary<string, string>
            {
                ["baseUrl"] = Settings.BaseUrl,
                ["httpStatus"] = result.StatusCode is null
                    ? "n/a"
                    : ((int)result.StatusCode.Value).ToString(CultureInfo.InvariantCulture),
                ["virtualHostScoped"] = string.IsNullOrWhiteSpace(Settings.VirtualHost) ? "false" : "true"
            },
            [
                "Confirm the RabbitMQ management plugin/API is reachable from this machine.",
                "Confirm the configured account has read-only access to the required management endpoints.",
                "ERP Doctor intentionally does not include the password, Authorization header, or error response body in evidence."
            ]);
}

internal sealed class RabbitMqOverviewCheck(RabbitMqSettings settings) : RabbitMqCheckBase(settings)
{
    public override string Id => "overview";
    public override string Name => "Management API overview";

    public override async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var result = await GetAsync("api/overview", cancellationToken);
        return result.Succeeded
            ? RabbitMqOverviewEvaluator.Evaluate(result.Body)
            : ApiFailure(result);
    }
}

internal sealed class RabbitMqNodesCheck(RabbitMqSettings settings) : RabbitMqCheckBase(settings)
{
    public override string Id => "nodes";
    public override string Name => "Node alarms and partitions";

    public override async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var result = await GetAsync("api/nodes", cancellationToken);
        return result.Succeeded
            ? RabbitMqNodeEvaluator.Evaluate(result.Body)
            : ApiFailure(result);
    }
}

internal sealed class RabbitMqQueuesCheck(RabbitMqSettings settings) : RabbitMqCheckBase(settings)
{
    public override string Id => "queues";
    public override string Name => "Queue backlog and consumers";

    public override async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var result = await GetAsync(RabbitMqApiClient.QueuePath(Settings), cancellationToken);
        return result.Succeeded
            ? RabbitMqQueueEvaluator.Evaluate(result.Body, Settings)
            : ApiFailure(result);
    }
}
