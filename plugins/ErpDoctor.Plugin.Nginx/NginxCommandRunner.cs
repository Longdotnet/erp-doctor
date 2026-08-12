using System.ComponentModel;
using System.Diagnostics;

namespace ErpDoctor.Plugin.Nginx;

internal sealed record NginxCommandResult(
    bool Succeeded,
    bool TimedOut,
    int? ExitCode,
    string Stdout,
    string Stderr,
    string FailureSummary);

internal sealed class NginxCommandRunner
{
    private const int MaxCapturedCharacters = 100_000;

    public async Task<NginxCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return new NginxCommandResult(
                false,
                false,
                null,
                string.Empty,
                string.Empty,
                $"Nginx CLI could not be started ({ex.GetType().Name}).");
        }

        if (process is null)
        {
            return new NginxCommandResult(
                false,
                false,
                null,
                string.Empty,
                string.Empty,
                "Nginx CLI process could not be started.");
        }

        using (process)
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await DrainAsync(stdoutTask, stderrTask);
                return new NginxCommandResult(
                    false,
                    true,
                    null,
                    string.Empty,
                    string.Empty,
                    $"Nginx CLI command exceeded the {timeoutSeconds}s timeout.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await DrainAsync(stdoutTask, stderrTask);
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (stdout.Length > MaxCapturedCharacters || stderr.Length > MaxCapturedCharacters)
            {
                return new NginxCommandResult(
                    false,
                    false,
                    process.ExitCode,
                    string.Empty,
                    string.Empty,
                    "Nginx CLI output exceeded the 100,000-character safety limit.");
            }

            return new NginxCommandResult(
                process.ExitCode == 0,
                false,
                process.ExitCode,
                stdout.Trim(),
                stderr.Trim(),
                process.ExitCode == 0
                    ? string.Empty
                    : $"Nginx CLI exited with code {process.ExitCode}.");
        }
    }

    private static async Task DrainAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch
        {
            // Best-effort drain after process termination; raw output is never surfaced.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }
}
