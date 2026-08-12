using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ErpDoctor.Core;

namespace ErpDoctor.Infrastructure.WindowsEventDiagnostics;

public sealed record WindowsEventSnapshot(
    DateTimeOffset? TimeCreatedUtc,
    string Provider,
    int EventId,
    int Level,
    string Computer,
    string Message);

public sealed class WindowsEventLogCheck(WindowsEventLogQueryOptions query) : IDiagnosticCheck
{
    public string Id => $"eventlog.{Normalize(query.Name)}";
    public string Name => query.Name;
    public string Category => "eventlog";

    public Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Skipped,
                "Windows Event Log diagnostics require Windows."));
        }

        if (string.IsNullOrWhiteSpace(query.LogName))
        {
            return Task.FromResult(new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Skipped,
                "No Windows Event Log channel configured."));
        }

        try
        {
            var events = WindowsEventLogReader.Read(query, cancellationToken);
            return Task.FromResult(WindowsEventLogEvaluator.Evaluate(query, events));
        }
        catch (Win32Exception ex)
        {
            return Task.FromResult(new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                $"Could not read Windows Event Log '{query.LogName}': {ex.Message}",
                Suggestions:
                [
                    "Confirm the log/channel exists and the current Windows account can read it.",
                    "Run ERP Doctor with the same account used for production support diagnostics."
                ]));
        }
    }

    private static string Normalize(string value) =>
        new string(value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray())
            .Trim('-');
}

public static class WindowsEventLogEvaluator
{
    public static DiagnosticResult Evaluate(
        WindowsEventLogQueryOptions query,
        IReadOnlyList<WindowsEventSnapshot> events)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(events);

        var maxEvents = Math.Clamp(query.MaxEvents, 1, 100);
        var matched = events
            .Where(item => MatchesProvider(query, item.Provider))
            .Take(maxEvents)
            .ToArray();
        var criticalCount = matched.Count(item => item.Level == 1);
        var errorCount = matched.Count(item => item.Level == 2);
        var warningCount = matched.Count(item => item.Level == 3);

        var status = criticalCount > 0
            ? DiagnosticStatus.Critical
            : errorCount > 0 || warningCount > 0
                ? DiagnosticStatus.Warning
                : DiagnosticStatus.Healthy;

        var summary = matched.Length == 0
            ? $"No matching recent events in {query.LogName} during the last {query.LookbackMinutes} minute(s)."
            : $"{matched.Length} recent event(s): {criticalCount} critical, {errorCount} error, {warningCount} warning.";

        var evidence = new Dictionary<string, string>
        {
            ["logName"] = query.LogName,
            ["lookbackMinutes"] = Math.Clamp(query.LookbackMinutes, 1, 10080).ToString(),
            ["eventCount"] = matched.Length.ToString()
        };

        if (query.Providers.Count > 0)
        {
            evidence["providers"] = string.Join(" | ", query.Providers);
        }

        for (var index = 0; index < matched.Length; index++)
        {
            evidence[$"event{index + 1}"] = RenderEvent(matched[index]);
        }

        IReadOnlyList<string>? suggestions = status == DiagnosticStatus.Healthy
            ? null
            : [
                "Correlate the event timestamp/provider with IIS, HTTP, SQL, disk, and runtime diagnostics from the same ERP Doctor run.",
                "Inspect the full event in Windows Event Viewer when the summarized message is not enough to identify the root cause.",
                "ERP Doctor reads Event Log entries only and never clears or modifies the log."
            ];

        return new DiagnosticResult(
            $"eventlog.{Normalize(query.Name)}",
            query.Name,
            status,
            summary,
            evidence,
            suggestions);
    }

    public static bool MatchesProvider(
        WindowsEventLogQueryOptions query,
        string provider)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Providers.Count == 0)
        {
            return true;
        }

        return query.Providers.Any(expected =>
            string.Equals(expected.Trim(), provider, StringComparison.OrdinalIgnoreCase));
    }

    private static string RenderEvent(WindowsEventSnapshot item)
    {
        var timestamp = item.TimeCreatedUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "unknown-time";
        var level = item.Level switch
        {
            1 => "Critical",
            2 => "Error",
            3 => "Warning",
            _ => $"Level {item.Level}"
        };
        var message = WindowsEventTextSanitizer.Sanitize(item.Message);
        return $"{timestamp} | {item.Provider} | ID {item.EventId} | {level} | {message}";
    }

    private static string Normalize(string value) =>
        new string(value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray())
            .Trim('-');
}

public static class WindowsEventXmlParser
{
    private static readonly XNamespace EventNamespace =
        "http://schemas.microsoft.com/win/2004/08/events/event";

    public static WindowsEventSnapshot Parse(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        var document = XDocument.Parse(xml, LoadOptions.None);
        var root = document.Root
            ?? throw new InvalidDataException("Windows event XML has no root element.");
        var system = root.Element(EventNamespace + "System")
            ?? throw new InvalidDataException("Windows event XML has no System element.");

        var provider = system.Element(EventNamespace + "Provider")
            ?.Attribute("Name")?.Value ?? string.Empty;
        var eventId = ParseInt(system.Element(EventNamespace + "EventID")?.Value);
        var level = ParseInt(system.Element(EventNamespace + "Level")?.Value);
        var computer = system.Element(EventNamespace + "Computer")?.Value ?? string.Empty;
        var timeRaw = system.Element(EventNamespace + "TimeCreated")
            ?.Attribute("SystemTime")?.Value;
        DateTimeOffset? time = DateTimeOffset.TryParse(timeRaw, out var parsedTime)
            ? parsedTime.ToUniversalTime()
            : null;

        var message = root
            .Element(EventNamespace + "RenderingInfo")
            ?.Element(EventNamespace + "Message")
            ?.Value;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = RenderEventData(root);
        }

        return new WindowsEventSnapshot(
            time,
            provider,
            eventId,
            level,
            computer,
            NormalizeWhitespace(message ?? string.Empty));
    }

    private static string RenderEventData(XElement root)
    {
        var values = root
            .Element(EventNamespace + "EventData")
            ?.Elements(EventNamespace + "Data")
            .Select(element =>
            {
                var name = element.Attribute("Name")?.Value;
                return string.IsNullOrWhiteSpace(name)
                    ? element.Value
                    : $"{name}={element.Value}";
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return values is { Length: > 0 }
            ? string.Join("; ", values)
            : "(message unavailable)";
    }

    private static int ParseInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : 0;

    private static string NormalizeWhitespace(string value)
    {
        var normalized = Regex.Replace(value, @"\s+", " ").Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500] + "...";
    }
}

internal static class WindowsEventTextSanitizer
{
    private static readonly Regex InlineSecretPattern = new(
        @"(?i)\b(password|pwd|token|secret|api[-_ ]?key|authorization)\b\s*[:=]\s*(?:Bearer\s+)?[^;,\s&]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Sanitize(string value)
    {
        var sanitized = InlineSecretPattern.Replace(value, "$1=[REDACTED]");
        sanitized = BearerPattern.Replace(sanitized, "Bearer [REDACTED]");
        return sanitized.Length <= 500 ? sanitized : sanitized[..500] + "...";
    }
}

internal static class WindowsEventLogReader
{
    private const int EvtQueryChannelPath = 0x1;
    private const int EvtQueryReverseDirection = 0x200;
    private const int EvtRenderEventXml = 1;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;

    public static IReadOnlyList<WindowsEventSnapshot> Read(
        WindowsEventLogQueryOptions options,
        CancellationToken cancellationToken)
    {
        var lookbackMinutes = Math.Clamp(options.LookbackMinutes, 1, 10080);
        var maxEvents = Math.Clamp(options.MaxEvents, 1, 100);
        var xpath = BuildXPath(lookbackMinutes, options.IncludeWarnings);
        var queryHandle = EvtQuery(
            IntPtr.Zero,
            options.LogName,
            xpath,
            EvtQueryChannelPath | EvtQueryReverseDirection);
        if (queryHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var events = new List<WindowsEventSnapshot>(maxEvents);
            var handles = new IntPtr[1];
            while (events.Count < maxEvents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var returned = 0;
                if (!EvtNext(queryHandle, handles.Length, handles, 0, 0, ref returned))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error);
                }

                for (var index = 0; index < returned; index++)
                {
                    var eventHandle = handles[index];
                    try
                    {
                        var parsed = WindowsEventXmlParser.Parse(RenderXml(eventHandle));
                        if (WindowsEventLogEvaluator.MatchesProvider(options, parsed.Provider))
                        {
                            events.Add(parsed);
                            if (events.Count >= maxEvents)
                            {
                                break;
                            }
                        }
                    }
                    finally
                    {
                        if (eventHandle != IntPtr.Zero)
                        {
                            EvtClose(eventHandle);
                            handles[index] = IntPtr.Zero;
                        }
                    }
                }
            }

            return events;
        }
        finally
        {
            EvtClose(queryHandle);
        }
    }

    private static string BuildXPath(int lookbackMinutes, bool includeWarnings)
    {
        var milliseconds = checked(lookbackMinutes * 60 * 1000);
        var levelExpression = includeWarnings
            ? "(Level=1 or Level=2 or Level=3)"
            : "(Level=1 or Level=2)";
        return $"*[System[{levelExpression} and TimeCreated[timediff(@SystemTime) <= {milliseconds}]]]";
    }

    private static string RenderXml(IntPtr eventHandle)
    {
        if (EvtRender(
                IntPtr.Zero,
                eventHandle,
                EvtRenderEventXml,
                0,
                IntPtr.Zero,
                out var bufferUsed,
                out _))
        {
            return string.Empty;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error);
        }

        var buffer = Marshal.AllocHGlobal(checked((int)bufferUsed));
        try
        {
            if (!EvtRender(
                    IntPtr.Zero,
                    eventHandle,
                    EvtRenderEventXml,
                    bufferUsed,
                    buffer,
                    out _,
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtQuery(
        IntPtr session,
        string path,
        string query,
        int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(
        IntPtr resultSet,
        int eventArraySize,
        [Out] IntPtr[] eventArray,
        int timeout,
        int flags,
        ref int returned);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(
        IntPtr context,
        IntPtr fragment,
        int flags,
        int bufferSize,
        IntPtr buffer,
        out int bufferUsed,
        out int propertyCount);

    [DllImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(IntPtr handle);
}
