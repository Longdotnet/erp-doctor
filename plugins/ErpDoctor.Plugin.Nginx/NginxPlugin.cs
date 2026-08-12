using ErpDoctor.PluginSdk;

namespace ErpDoctor.Plugin.Nginx;

public sealed class NginxPlugin : IErpDoctorPlugin
{
    public string Id => "nginx";
    public string Name => "Nginx Diagnostics";
    public string Version => "0.2.0";

    public IReadOnlyList<IPluginCheck> CreateChecks(PluginContext context)
    {
        var settings = NginxSettings.From(context.Configuration);
        return
        [
            new NginxVersionCheck(settings),
            new NginxConfigCheck(settings)
        ];
    }
}

internal abstract class NginxCheckBase(NginxSettings settings) : IPluginCheck
{
    private readonly NginxCommandRunner _runner = new();

    protected NginxSettings Settings { get; } = settings;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public string Category => "nginx";

    public abstract Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken);

    protected Task<NginxCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        _runner.RunAsync(
            Settings.Executable,
            arguments,
            Settings.CommandTimeoutSeconds,
            cancellationToken);
}

internal sealed class NginxVersionCheck(NginxSettings settings) : NginxCheckBase(settings)
{
    public override string Id => "version";
    public override string Name => "Nginx version";

    public override async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;

        if (!OperatingSystem.IsLinux())
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Skipped,
                "Nginx diagnostics require Linux.");
        }

        var result = await RunAsync(["-v"], cancellationToken);
        return NginxVersionEvaluator.Evaluate(result);
    }
}

internal sealed class NginxConfigCheck(NginxSettings settings) : NginxCheckBase(settings)
{
    public override string Id => "config";
    public override string Name => "Nginx configuration";

    public override async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;

        if (!OperatingSystem.IsLinux())
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Skipped,
                "Nginx configuration diagnostics require Linux.");
        }

        var arguments = new List<string> { "-t", "-q" };
        if (!string.IsNullOrWhiteSpace(Settings.ConfigPath))
        {
            arguments.Add("-c");
            arguments.Add(Settings.ConfigPath);
        }

        var result = await RunAsync(arguments, cancellationToken);
        return NginxConfigEvaluator.Evaluate(result, Settings.ConfigPath);
    }
}
