using System.Text.RegularExpressions;
using ErpDoctor.PluginSdk;

namespace ErpDoctor.Plugin.Nginx;

internal static partial class NginxVersionEvaluator
{
    [GeneratedRegex(@"nginx(?:\s+version)?\s*:\s*nginx/(?<version>[^\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    public static PluginDiagnosticResult Evaluate(NginxCommandResult result)
    {
        if (!result.Succeeded)
        {
            return Failure(result, "Nginx version check failed.");
        }

        var combined = string.Concat(result.Stdout, "\n", result.Stderr);
        var match = VersionRegex().Match(combined);
        var version = match.Success ? match.Groups["version"].Value : "unknown";

        return new PluginDiagnosticResult(
            PluginDiagnosticStatus.Healthy,
            version == "unknown"
                ? "Nginx CLI is available; version string could not be parsed."
                : $"Nginx {version} is available.",
            new Dictionary<string, string>
            {
                ["nginxVersion"] = version
            });
    }

    private static PluginDiagnosticResult Failure(NginxCommandResult result, string summary) =>
        new(
            PluginDiagnosticStatus.Error,
            string.IsNullOrWhiteSpace(result.FailureSummary) ? summary : result.FailureSummary,
            new Dictionary<string, string>
            {
                ["timedOut"] = result.TimedOut ? "true" : "false",
                ["exitCode"] = result.ExitCode?.ToString() ?? "n/a"
            },
            [
                "Confirm the Nginx executable is installed and accessible to the ERP Doctor process.",
                "Raw Nginx stdout/stderr is intentionally not copied into failure evidence."
            ]);
}

internal static class NginxConfigEvaluator
{
    public static PluginDiagnosticResult Evaluate(
        NginxCommandResult result,
        string? configuredPath)
    {
        if (!result.Succeeded)
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Critical,
                string.IsNullOrWhiteSpace(result.FailureSummary)
                    ? "Nginx configuration validation failed."
                    : result.FailureSummary,
                new Dictionary<string, string>
                {
                    ["configPath"] = configuredPath ?? "default",
                    ["timedOut"] = result.TimedOut ? "true" : "false",
                    ["exitCode"] = result.ExitCode?.ToString() ?? "n/a"
                },
                [
                    "Run the same Nginx configuration test manually to inspect detailed syntax/file errors.",
                    "ERP Doctor does not reload or restart Nginx after a configuration failure."
                ]);
        }

        return new PluginDiagnosticResult(
            PluginDiagnosticStatus.Healthy,
            configuredPath is null
                ? "Nginx default configuration passed validation."
                : "Configured Nginx configuration file passed validation.",
            new Dictionary<string, string>
            {
                ["configPath"] = configuredPath ?? "default"
            });
    }
}
