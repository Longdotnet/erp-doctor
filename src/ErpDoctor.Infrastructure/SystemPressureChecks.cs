using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ErpDoctor.Core;

namespace ErpDoctor.Infrastructure.SystemDiagnostics;

public sealed class CpuUtilizationCheck : IDiagnosticCheck
{
    public string Id => "system.cpu";
    public string Name => "System CPU";
    public string Category => "system";

    public async Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        var sampleMilliseconds = Math.Clamp(
            context.Options.System.CpuSampleMilliseconds,
            100,
            2_000);

        if (!TryReadCpuTimes(out var first, out var error))
        {
            return error!;
        }

        await Task.Delay(sampleMilliseconds, cancellationToken);

        if (!TryReadCpuTimes(out var second, out error))
        {
            return error!;
        }

        if (second.Total <= first.Total || second.Idle < first.Idle)
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                "CPU counters did not advance as expected.");
        }

        var totalDelta = second.Total - first.Total;
        var idleDelta = second.Idle - first.Idle;
        var busyDelta = totalDelta > idleDelta ? totalDelta - idleDelta : 0;
        var utilization = Math.Clamp((double)busyDelta / totalDelta * 100d, 0d, 100d);
        return CpuPressureEvaluator.Evaluate(
            utilization,
            sampleMilliseconds,
            Environment.ProcessorCount,
            context.Options.System);
    }

    private bool TryReadCpuTimes(out CpuTimes times, out DiagnosticResult? error)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                times = default;
                error = new DiagnosticResult(
                    Id,
                    Name,
                    DiagnosticStatus.Error,
                    "Windows could not report system CPU counters.");
                return false;
            }

            times = new CpuTimes(ToUInt64(idle), ToUInt64(kernel) + ToUInt64(user));
            error = null;
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var firstLine = File.ReadLines("/proc/stat").FirstOrDefault();
                if (LinuxCpuParser.TryParse(firstLine, out times))
                {
                    error = null;
                    return true;
                }

                error = new DiagnosticResult(
                    Id,
                    Name,
                    DiagnosticStatus.Error,
                    "Could not parse Linux /proc/stat CPU counters.");
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                times = default;
                error = new DiagnosticResult(
                    Id,
                    Name,
                    DiagnosticStatus.Error,
                    $"Linux CPU counters could not be read ({ex.GetType().Name}).");
                return false;
            }
        }

        times = default;
        error = new DiagnosticResult(
            Id,
            Name,
            DiagnosticStatus.Skipped,
            "System CPU diagnostics are currently implemented for Windows and Linux.");
        return false;
    }

    private static ulong ToUInt64(FILETIME value) =>
        ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FILETIME idleTime,
        out FILETIME kernelTime,
        out FILETIME userTime);
}

public sealed class LoadAverageCheck : IDiagnosticCheck
{
    public string Id => "system.load";
    public string Name => "System load average";
    public string Category => "system";

    public async Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Skipped,
                "Load-average diagnostics are available on Linux.");
        }

        try
        {
            var raw = await File.ReadAllTextAsync("/proc/loadavg", cancellationToken);
            if (!LinuxLoadAverageParser.TryParse(raw, out var snapshot))
            {
                return new DiagnosticResult(
                    Id,
                    Name,
                    DiagnosticStatus.Error,
                    "Could not parse Linux /proc/loadavg.");
            }

            return LoadAverageEvaluator.Evaluate(
                snapshot,
                Environment.ProcessorCount,
                context.Options.System);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                $"Linux load-average counters could not be read ({ex.GetType().Name}).");
        }
    }
}

public sealed class TopProcessesCheck : IDiagnosticCheck
{
    public string Id => "system.processes";
    public string Name => "Top processes by memory";
    public string Category => "system";

    public Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = Math.Clamp(context.Options.System.TopProcessesLimit, 1, 20);
        var snapshots = new List<ProcessResourceSnapshot>();
        Process[] processes;

        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                $"Processes could not be enumerated ({ex.GetType().Name})."));
        }

        foreach (var process in processes)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                snapshots.Add(new ProcessResourceSnapshot(
                    process.Id,
                    SanitizeProcessName(process.ProcessName),
                    Math.Max(0, process.WorkingSet64)));
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Processes can exit or deny access while the snapshot is being collected.
            }
            finally
            {
                process.Dispose();
            }
        }

        var top = snapshots
            .OrderByDescending(snapshot => snapshot.WorkingSetBytes)
            .ThenBy(snapshot => snapshot.ProcessId)
            .Take(limit)
            .ToArray();

        var evidence = new Dictionary<string, string>
        {
            ["processCount"] = snapshots.Count.ToString(CultureInfo.InvariantCulture),
            ["topProcessLimit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["topProcesses"] = ProcessSnapshotFormatter.Format(top)
        };

        return Task.FromResult(new DiagnosticResult(
            Id,
            Name,
            DiagnosticStatus.Info,
            top.Length == 0
                ? "No process working-set data was available."
                : $"Captured the top {top.Length} process(es) by working-set memory.",
            evidence,
            ["Use this bounded snapshot to identify resource-heavy processes; ERP Doctor does not read process command lines or environment variables."]));
    }

    private static string SanitizeProcessName(string value)
    {
        var safe = new string(value
            .Where(ch => !char.IsControl(ch) && ch is not '|' and not ';')
            .Take(64)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }
}

internal readonly record struct CpuTimes(ulong Idle, ulong Total);
internal readonly record struct LoadAverageSnapshot(double Load1, double Load5, double Load15);
internal readonly record struct ProcessResourceSnapshot(int ProcessId, string Name, long WorkingSetBytes);

internal static class LinuxCpuParser
{
    public static bool TryParse(string? line, out CpuTimes times)
    {
        times = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || !parts[0].Equals("cpu", StringComparison.Ordinal))
        {
            return false;
        }

        var values = new List<ulong>(parts.Length - 1);
        foreach (var token in parts.Skip(1))
        {
            if (!ulong.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            values.Add(value);
        }

        if (values.Count < 4)
        {
            return false;
        }

        var total = values.Aggregate(0UL, static (sum, value) => sum + value);
        var idle = values[3] + (values.Count > 4 ? values[4] : 0UL);
        times = new CpuTimes(idle, total);
        return total > 0;
    }
}

internal static class CpuPressureEvaluator
{
    public static DiagnosticResult Evaluate(
        double utilizationPercent,
        int sampleMilliseconds,
        int processorCount,
        SystemOptions options)
    {
        var warning = Math.Clamp(options.CpuWarningPercent, 1d, 100d);
        var critical = Math.Clamp(options.CpuCriticalPercent, warning, 100d);
        var utilization = Math.Clamp(utilizationPercent, 0d, 100d);
        var status = utilization >= critical
            ? DiagnosticStatus.Critical
            : utilization >= warning
                ? DiagnosticStatus.Warning
                : DiagnosticStatus.Healthy;

        return new DiagnosticResult(
            "system.cpu",
            "System CPU",
            status,
            $"CPU utilization is {utilization:F1}% over a {sampleMilliseconds} ms sample.",
            new Dictionary<string, string>
            {
                ["utilizationPercent"] = utilization.ToString("F1", CultureInfo.InvariantCulture),
                ["sampleMilliseconds"] = sampleMilliseconds.ToString(CultureInfo.InvariantCulture),
                ["processorCount"] = Math.Max(1, processorCount).ToString(CultureInfo.InvariantCulture)
            },
            status is DiagnosticStatus.Warning or DiagnosticStatus.Critical
                ? ["Inspect sustained CPU pressure and the bounded process snapshot before restarting application or database services."]
                : null);
    }
}

internal static class LinuxLoadAverageParser
{
    public static bool TryParse(string? raw, out LoadAverageSnapshot snapshot)
    {
        snapshot = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var load1) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var load5) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var load15))
        {
            return false;
        }

        snapshot = new LoadAverageSnapshot(
            Math.Max(0d, load1),
            Math.Max(0d, load5),
            Math.Max(0d, load15));
        return true;
    }
}

internal static class LoadAverageEvaluator
{
    public static DiagnosticResult Evaluate(
        LoadAverageSnapshot snapshot,
        int processorCount,
        SystemOptions options)
    {
        var processors = Math.Max(1, processorCount);
        var loadPerCpu = snapshot.Load1 / processors;
        var warning = Math.Clamp(options.LoadPerCpuWarning, 0.1d, 100d);
        var critical = Math.Clamp(options.LoadPerCpuCritical, warning, 200d);
        var status = loadPerCpu >= critical
            ? DiagnosticStatus.Critical
            : loadPerCpu >= warning
                ? DiagnosticStatus.Warning
                : DiagnosticStatus.Healthy;

        return new DiagnosticResult(
            "system.load",
            "System load average",
            status,
            $"Linux 1-minute load per CPU is {loadPerCpu:F2} ({snapshot.Load1:F2} total across {processors} logical processor(s)).",
            new Dictionary<string, string>
            {
                ["processorCount"] = processors.ToString(CultureInfo.InvariantCulture),
                ["load1"] = snapshot.Load1.ToString("F2", CultureInfo.InvariantCulture),
                ["load5"] = snapshot.Load5.ToString("F2", CultureInfo.InvariantCulture),
                ["load15"] = snapshot.Load15.ToString("F2", CultureInfo.InvariantCulture),
                ["load1PerCpu"] = loadPerCpu.ToString("F2", CultureInfo.InvariantCulture)
            },
            status is DiagnosticStatus.Warning or DiagnosticStatus.Critical
                ? ["Compare 1/5/15-minute load to distinguish a short spike from sustained CPU or I/O pressure."]
                : null);
    }
}

internal static class ProcessSnapshotFormatter
{
    public static string Format(IReadOnlyList<ProcessResourceSnapshot> snapshots) =>
        string.Join(
            " | ",
            snapshots.Select(snapshot =>
                $"{snapshot.ProcessId}:{snapshot.Name}:{snapshot.WorkingSetBytes / 1024d / 1024d:F1}MB"));
}
