using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace ErpDoctor.Plugin.Redis;

internal sealed record RedisCliResult(
    bool Succeeded,
    bool TimedOut,
    int? ExitCode,
    string Stdout,
    string FailureSummary)
{
    public static RedisCliResult Success(string stdout) =>
        new(true, false, 0, stdout, string.Empty);

    public static RedisCliResult Failure(
        string summary,
        int? exitCode = null,
        bool timedOut = false) =>
        new(false, timedOut, exitCode, string.Empty, summary);
}

internal sealed class RedisCli
{
    private const int MaxCapturedCharacters = 1_000_000;

    public async Task<RedisCliResult> RunAsync(
        RedisSettings settings,
        IReadOnlyList<string> commandArguments,
        CancellationToken cancellationToken)
    {
        var credential = ResolveCredential(settings);
        if (!credential.Succeeded)
        {
            return RedisCliResult.Failure(credential.FailureSummary);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = settings.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--raw");
        startInfo.ArgumentList.Add("-h");
        startInfo.ArgumentList.Add(settings.Host);
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(settings.Port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(settings.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture));

        if (settings.UseTls)
        {
            startInfo.ArgumentList.Add("--tls");
            if (!string.IsNullOrWhiteSpace(settings.CaCertificatePath))
            {
                startInfo.ArgumentList.Add("--cacert");
                startInfo.ArgumentList.Add(settings.CaCertificatePath);
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.Username) && credential.Password is not null)
        {
            startInfo.ArgumentList.Add("--user");
            startInfo.ArgumentList.Add(settings.Username);
        }

        foreach (var argument in commandArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Never inherit ambient Redis auth accidentally. Credentials are opt-in via plugin config.
        startInfo.Environment.Remove("REDISCLI_AUTH");
        if (credential.Password is not null)
        {
            startInfo.Environment["REDISCLI_AUTH"] = credential.Password;
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return RedisCliResult.Failure(
                $"redis-cli could not be started ({ex.GetType().Name}).");
        }

        if (process is null)
        {
            return RedisCliResult.Failure("redis-cli process could not be started.");
        }

        using (process)
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.CommandTimeoutSeconds));

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
                return RedisCliResult.Failure(
                    $"redis-cli command exceeded the {settings.CommandTimeoutSeconds}s timeout.",
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
                return RedisCliResult.Failure(
                    "redis-cli output exceeded the 1,000,000-character safety limit.",
                    process.ExitCode);
            }

            var classifiedFailure = ClassifyRedisFailure(stdout, stderr);
            if (process.ExitCode != 0 || classifiedFailure is not null)
            {
                return RedisCliResult.Failure(
                    classifiedFailure ?? $"redis-cli exited with code {process.ExitCode}.",
                    process.ExitCode);
            }

            return RedisCliResult.Success(stdout.Trim());
        }
    }

    private static RedisCredentialResolution ResolveCredential(RedisSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PasswordEnvironmentVariable))
        {
            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                return RedisCredentialResolution.Failure(
                    "Redis ACL username is configured without a password environment variable.");
            }

            return RedisCredentialResolution.Success(null);
        }

        var password = Environment.GetEnvironmentVariable(settings.PasswordEnvironmentVariable);
        if (string.IsNullOrEmpty(password))
        {
            return RedisCredentialResolution.Failure(
                $"Redis password environment variable '{settings.PasswordEnvironmentVariable}' is not set.");
        }

        return RedisCredentialResolution.Success(password);
    }

    internal static string? ClassifyRedisFailure(string stdout, string stderr) =>
        ClassifyRedisErrorLine(FirstMeaningfulLine(stdout)) ??
        ClassifyRedisErrorLine(FirstMeaningfulLine(stderr));

    private static string? FirstMeaningfulLine(string value)
    {
        foreach (var rawLine in value.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return null;
    }

    private static string? ClassifyRedisErrorLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        if (StartsWithToken(line, "NOPERM"))
        {
            return "Redis denied the diagnostic command through ACL permissions.";
        }

        if (StartsWithToken(line, "NOAUTH") || StartsWithToken(line, "WRONGPASS"))
        {
            return "Redis authentication failed.";
        }

        if (StartsWithToken(line, "LOADING"))
        {
            return "Redis is loading data and cannot serve the diagnostic command yet.";
        }

        if (StartsWithToken(line, "MISCONF"))
        {
            return "Redis reported a configuration error while serving the diagnostic command.";
        }

        if (line.StartsWith("ERR ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("(error)", StringComparison.OrdinalIgnoreCase))
        {
            return "Redis returned an error response for the diagnostic command.";
        }

        return null;
    }

    private static bool StartsWithToken(string value, string token) =>
        value.StartsWith(token, StringComparison.OrdinalIgnoreCase) &&
        (value.Length == token.Length || char.IsWhiteSpace(value[token.Length]));

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

    private sealed record RedisCredentialResolution(
        bool Succeeded,
        string? Password,
        string FailureSummary)
    {
        public static RedisCredentialResolution Success(string? password) =>
            new(true, password, string.Empty);

        public static RedisCredentialResolution Failure(string summary) =>
            new(false, null, summary);
    }
}
