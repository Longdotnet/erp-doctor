using System.ComponentModel;
using System.Diagnostics;

namespace ErpDoctor.Plugin.Docker;

internal sealed record DockerCliResult(
    bool Succeeded,
    bool TimedOut,
    int? ExitCode,
    string Stdout,
    string FailureSummary)
{
    public static DockerCliResult Success(string stdout) =>
        new(true, false, 0, stdout, string.Empty);

    public static DockerCliResult Failure(
        string summary,
        int? exitCode = null,
        bool timedOut = false) =>
        new(false, timedOut, exitCode, string.Empty, summary);
}

internal sealed class DockerCli
{
    private const int MaxCapturedCharacters = 1_000_000;

    public async Task<DockerCliResult> RunAsync(
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
            return DockerCliResult.Failure(
                $"Docker CLI could not be started ({ex.GetType().Name}).");
        }

        if (process is null)
        {
            return DockerCliResult.Failure("Docker CLI process could not be started.");
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
                return DockerCliResult.Failure(
                    $"Docker CLI command exceeded the {timeoutSeconds}s timeout.",
                    timedOut: true);
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
                return DockerCliResult.Failure(
                    "Docker CLI output exceeded the 1,000,000-character safety limit.",
                    process.ExitCode);
            }

            if (process.ExitCode != 0)
            {
                return DockerCliResult.Failure(
                    $"Docker CLI exited with code {process.ExitCode}.",
                    process.ExitCode);
            }

            return DockerCliResult.Success(stdout.Trim());
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
            // Best-effort drain after process termination. Raw output is never surfaced.
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
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
