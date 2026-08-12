using System.Globalization;
using System.Text.Json;
using ErpDoctor.PluginSdk;

namespace ErpDoctor.Plugin.Docker;

public sealed class DockerPlugin : IErpDoctorPlugin
{
    public string Id => "docker";
    public string Name => "Docker Diagnostics";
    public string Version => "0.1.0";

    public IReadOnlyList<IPluginCheck> CreateChecks(PluginContext context)
    {
        var settings = DockerSettings.From(context.Configuration);
        return
        [
            new DockerEngineCheck(settings),
            new DockerInfoCheck(settings),
            new DockerContainersCheck(settings)
        ];
    }
}

internal abstract class DockerCheckBase(DockerSettings settings) : IPluginCheck
{
    private readonly DockerCli _cli = new();

    protected DockerSettings Settings { get; } = settings;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public string Category => "docker";

    public abstract Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken);

    protected Task<DockerCliResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        _cli.RunAsync(
            Settings.Executable,
            arguments,
            Settings.CommandTimeoutSeconds,
            cancellationToken);

    protected PluginDiagnosticResult CliFailure(DockerCliResult result) =>
        new(
            PluginDiagnosticStatus.Error,
            result.FailureSummary,
            new Dictionary<string, string>
            {
                ["dockerExecutable"] = Settings.Executable,
                ["timedOut"] = result.TimedOut ? "true" : "false",
                ["exitCode"] = result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a"
            },
            [
                "Confirm the Docker CLI is installed and the current Docker context can reach the intended engine.",
                "ERP Doctor intentionally does not include raw Docker stderr in diagnostic evidence."
            ]);

    protected static PluginDiagnosticResult InvalidJson(string checkName) =>
        new(
            PluginDiagnosticStatus.Error,
            $"Docker {checkName} returned unexpected JSON output.",
            Suggestions:
            [
                "Confirm the installed Docker CLI supports JSON formatting for this command.",
                "Run the equivalent Docker command manually if deeper troubleshooting is required."
            ]);
}

internal sealed class DockerEngineCheck(DockerSettings settings) : DockerCheckBase(settings)
{
    public override string Id => "engine";
    public override string Name => "Engine connectivity";

    public override async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var result = await RunAsync(
            ["version", "--format", "json"],
            cancellationToken);
        if (!result.Succeeded)
        {
            return CliFailure(result);
        }

        try
        {
            return DockerEngineEvaluator.Evaluate(result.Stdout);
        }
        catch (JsonException)
        {
            return InvalidJson("version");
        }
    }
}

internal sealed class DockerInfoCheck(DockerSettings settings) : DockerCheckBase(settings)
{
    public override string Id => "info";
    public override string Name => "Engine summary";

    public override async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var result = await RunAsync(
            ["info", "--format", "json"],
            cancellationToken);
        if (!result.Succeeded)
        {
            return CliFailure(result);
        }

        try
        {
            return DockerInfoEvaluator.Evaluate(result.Stdout);
        }
        catch (JsonException)
        {
            return InvalidJson("info");
        }
    }
}

internal sealed class DockerContainersCheck(DockerSettings settings) : DockerCheckBase(settings)
{
    public override string Id => "containers";
    public override string Name => "Container state and health";

    public override async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var result = await RunAsync(
            ["ps", "--all", "--format", "json"],
            cancellationToken);
        if (!result.Succeeded)
        {
            return CliFailure(result);
        }

        try
        {
            var containers = DockerContainerParser.ParseLines(result.Stdout);
            return DockerContainerEvaluator.Evaluate(containers, Settings);
        }
        catch (JsonException)
        {
            return InvalidJson("container list");
        }
    }
}
