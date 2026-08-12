using System.Runtime.InteropServices;
using ErpDoctor.Core;

namespace ErpDoctor.Infrastructure.SystemDiagnostics;

public sealed class DiskSpaceCheck(DriveInfo drive) : IDiagnosticCheck
{
    public string Id => $"system.disk.{NormalizeDriveId(drive.Name)}";
    public string Name => $"Disk space ({drive.Name})";
    public string Category => "system";

    public Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        if (!drive.IsReady)
        {
            return Task.FromResult(new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Skipped,
                $"Drive {drive.Name} is not ready."));
        }

        var total = drive.TotalSize;
        var free = drive.AvailableFreeSpace;
        var freePercent = total <= 0 ? 0 : (double)free / total * 100;

        var status = freePercent <= context.Options.System.DiskCriticalFreePercent
            ? DiagnosticStatus.Critical
            : freePercent <= context.Options.System.DiskWarningFreePercent
                ? DiagnosticStatus.Warning
                : DiagnosticStatus.Healthy;

        return Task.FromResult(new DiagnosticResult(
            Id,
            Name,
            status,
            $"{FormatBytes(free)} free of {FormatBytes(total)} ({freePercent:F1}% free)",
            new Dictionary<string, string>
            {
                ["drive"] = drive.Name,
                ["freeBytes"] = free.ToString(),
                ["totalBytes"] = total.ToString(),
                ["freePercent"] = freePercent.ToString("F2")
            },
            status is DiagnosticStatus.Warning or DiagnosticStatus.Critical
                ? ["Free disk space and identify rapidly growing logs, backups, temp files, or database files."]
                : null));
    }

    private static string NormalizeDriveId(string value) =>
        new(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string FormatBytes(long value)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        double size = value;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:F1} {units[unit]}";
    }
}

public sealed class DotNetRuntimeCheck : IDiagnosticCheck
{
    public string Id => "system.dotnet-runtime";
    public string Name => ".NET runtime";
    public string Category => "system";

    public Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        var description = RuntimeInformation.FrameworkDescription;
        return Task.FromResult(new DiagnosticResult(
            Id,
            Name,
            DiagnosticStatus.Healthy,
            description,
            new Dictionary<string, string>
            {
                ["framework"] = description,
                ["os"] = RuntimeInformation.OSDescription,
                ["architecture"] = RuntimeInformation.OSArchitecture.ToString()
            }));
    }
}

public sealed class MemoryCheck : IDiagnosticCheck
{
    public string Id => "system.memory";
    public string Name => "System memory";
    public string Category => "system";

    public Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return Task.FromResult(ReadWindowsMemory(context));
        }

        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            return Task.FromResult(ReadLinuxMemory(context));
        }

        return Task.FromResult(new DiagnosticResult(
            Id,
            Name,
            DiagnosticStatus.Skipped,
            "System memory diagnostics are currently implemented for Windows and Linux."));
    }

    private DiagnosticResult ReadWindowsMemory(DiagnosticContext context)
    {
        var state = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(state))
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                "Windows could not report memory status.");
        }

        return BuildResult(context, state.TotalPhys, state.AvailPhys);
    }

    private DiagnosticResult ReadLinuxMemory(DiagnosticContext context)
    {
        var values = File.ReadLines("/proc/meminfo")
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => parts[0],
                parts => ParseKilobytes(parts[1]),
                StringComparer.OrdinalIgnoreCase);

        if (!values.TryGetValue("MemTotal", out var totalKb) ||
            !values.TryGetValue("MemAvailable", out var availableKb))
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                "Could not parse /proc/meminfo.");
        }

        return BuildResult(context, totalKb * 1024, availableKb * 1024);
    }

    private DiagnosticResult BuildResult(
        DiagnosticContext context,
        ulong total,
        ulong available)
    {
        var percent = total == 0 ? 0 : (double)available / total * 100;
        var status = percent <= context.Options.System.MemoryWarningAvailablePercent
            ? DiagnosticStatus.Warning
            : DiagnosticStatus.Healthy;

        return new DiagnosticResult(
            Id,
            Name,
            status,
            $"{FormatBytes(available)} available of {FormatBytes(total)} ({percent:F1}% available)",
            new Dictionary<string, string>
            {
                ["availableBytes"] = available.ToString(),
                ["totalBytes"] = total.ToString(),
                ["availablePercent"] = percent.ToString("F2")
            },
            status == DiagnosticStatus.Warning
                ? ["Inspect memory-heavy processes and confirm whether SQL Server or the application is under memory pressure."]
                : null);
    }

    private static ulong ParseKilobytes(string raw)
    {
        var token = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return ulong.TryParse(token, out var value) ? value : 0;
    }

    private static string FormatBytes(ulong value)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:F1} {units[unit]}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
