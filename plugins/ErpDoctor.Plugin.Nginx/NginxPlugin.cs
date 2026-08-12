using ErpDoctor.PluginSdk;

namespace ErpDoctor.Plugin.Nginx;

public sealed class NginxPlugin : IErpDoctorPlugin
{
    public string Id => "nginx";
    public string Name => "Linux / Nginx Diagnostics";
    public string Version => "0.1.0";

    public IReadOnlyList<IPluginCheck> CreateChecks(PluginContext context)
    {
        var settings = NginxSettings.From(context.Configuration);
        return
        [
            new LinuxRuntimeCheck(settings),
            new NginxVersionCheck(settings),
            new NginxConfigCheck(settings)
        ];
    }
}

internal sealed class LinuxRuntimeCheck(NginxSettings settings) : IPluginCheck
{
    public string Id => "linux-runtime";
    public string Name => "Linux runtime";
    public string Category => "linux";

    public async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;

        if (!OperatingSystem.IsLinux())
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Skipped,
                "Linux runtime diagnostics require Linux.");
        }

        try
        {
            var osReleaseTask = File.ReadAllTextAsync("/etc/os-release", cancellationToken);
            var uptimeTask = File.ReadAllTextAsync("/proc/uptime", cancellationToken);
            var loadavgTask = File.ReadAllTextAsync("/proc/loadavg", cancellationToken);
            var meminfoTask = File.ReadAllTextAsync("/proc/meminfo", cancellationToken);

            await Task.WhenAll(osReleaseTask, uptimeTask, loadavgTask, meminfoTask);

            var snapshot = LinuxSnapshotParser.Parse(
                await osReleaseTask,
                await uptimeTask,
                await loadavgTask,
                await meminfoTask,
                Environment.ProcessorCount);

            return LinuxSnapshotEvaluator.Evaluate(snapshot, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Error,
                $"Linux runtime files could not be read ({ex.GetType().Name}).",
                Suggestions:
                [
                    "Confirm /etc/os-release and /proc runtime files are readable by the ERP Doctor process."
                ]);
        }
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
