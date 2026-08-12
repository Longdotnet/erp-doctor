using System.Diagnostics;
using ErpDoctor.Core;

namespace ErpDoctor.Infrastructure.IisDiagnostics;

public sealed class IisAppPoolCheck(string appPoolName) : IDiagnosticCheck
{
    public string Id => $"iis.apppool.{Normalize(appPoolName)}";
    public string Name => $"IIS AppPool {appPoolName}";
    public string Category => "iis";

    public async Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Skipped,
                "IIS diagnostics require Windows.");
        }

        if (string.IsNullOrWhiteSpace(appPoolName))
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Skipped,
                "No application pool name configured.");
        }

        var escapedName = appPoolName.Replace("'", "''", StringComparison.Ordinal);
        var command =
            $"Import-Module WebAdministration; (Get-WebAppPoolState -Name '{escapedName}').Value";

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                "Could not start PowerShell.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                string.IsNullOrWhiteSpace(stderr)
                    ? $"PowerShell exited with code {process.ExitCode}."
                    : stderr,
                Suggestions:
                [
                    "Confirm IIS and the WebAdministration PowerShell module are installed.",
                    "Run the terminal with sufficient permission to inspect IIS."
                ]);
        }

        var isStarted = string.Equals(stdout, "Started", StringComparison.OrdinalIgnoreCase);
        return new DiagnosticResult(
            Id,
            Name,
            isStarted ? DiagnosticStatus.Healthy : DiagnosticStatus.Critical,
            $"AppPool state: {stdout}",
            new Dictionary<string, string>
            {
                ["appPool"] = appPoolName,
                ["state"] = stdout
            },
            isStarted
                ? null
                : [
                    "Inspect Windows Event Viewer and application startup logs.",
                    "Check the AppPool identity, runtime configuration, disk space, and application dependencies.",
                    "ERP Doctor does not restart AppPools automatically."
                ]);
    }

    private static string Normalize(string value) =>
        new(value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray())
            .Trim('-');
}
