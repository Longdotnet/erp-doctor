using System.Globalization;
using System.Text.Json;
using ErpDoctor.PluginSdk;

namespace ErpDoctor.Plugin.Docker;

internal sealed record DockerContainerSnapshot(
    string Name,
    string State,
    string HealthStatus);

internal static class DockerJson
{
    public static string? GetString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    public static int GetInt32(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value))
        {
            return value;
        }

        return 0;
    }

    public static int GetArrayCount(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Array)
        {
            return property.GetArrayLength();
        }

        return 0;
    }
}

internal static class DockerEngineEvaluator
{
    public static PluginDiagnosticResult Evaluate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("Server", out var server) ||
            server.ValueKind != JsonValueKind.Object)
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Error,
                "Docker CLI responded, but Docker Engine server metadata is unavailable.",
                Suggestions:
                [
                    "Confirm the Docker daemon is running and the current Docker context can reach it."
                ]);
        }

        var version = DockerJson.GetString(server, "Version") ?? "unknown";
        var apiVersion = DockerJson.GetString(server, "ApiVersion", "APIVersion") ?? "unknown";
        var os = DockerJson.GetString(server, "Os", "OS") ?? "unknown";
        var arch = DockerJson.GetString(server, "Arch") ?? "unknown";

        return new PluginDiagnosticResult(
            PluginDiagnosticStatus.Healthy,
            $"Docker Engine {version} is reachable.",
            new Dictionary<string, string>
            {
                ["serverVersion"] = version,
                ["apiVersion"] = apiVersion,
                ["os"] = os,
                ["architecture"] = arch
            });
    }
}

internal static class DockerInfoEvaluator
{
    public static PluginDiagnosticResult Evaluate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var containers = DockerJson.GetInt32(root, "Containers");
        var running = DockerJson.GetInt32(root, "ContainersRunning");
        var paused = DockerJson.GetInt32(root, "ContainersPaused");
        var stopped = DockerJson.GetInt32(root, "ContainersStopped");
        var images = DockerJson.GetInt32(root, "Images");
        var warnings = DockerJson.GetArrayCount(root, "Warnings");
        var serverVersion = DockerJson.GetString(root, "ServerVersion") ?? "unknown";
        var osType = DockerJson.GetString(root, "OSType") ?? "unknown";
        var architecture = DockerJson.GetString(root, "Architecture") ?? "unknown";

        return new PluginDiagnosticResult(
            warnings > 0 ? PluginDiagnosticStatus.Warning : PluginDiagnosticStatus.Healthy,
            warnings > 0
                ? $"Docker Engine reports {warnings} warning(s); {running}/{containers} containers running."
                : $"Docker Engine reports {running}/{containers} containers running and {images} image(s).",
            new Dictionary<string, string>
            {
                ["serverVersion"] = serverVersion,
                ["containers"] = containers.ToString(CultureInfo.InvariantCulture),
                ["running"] = running.ToString(CultureInfo.InvariantCulture),
                ["paused"] = paused.ToString(CultureInfo.InvariantCulture),
                ["stopped"] = stopped.ToString(CultureInfo.InvariantCulture),
                ["images"] = images.ToString(CultureInfo.InvariantCulture),
                ["warningCount"] = warnings.ToString(CultureInfo.InvariantCulture),
                ["osType"] = osType,
                ["architecture"] = architecture
            },
            warnings > 0
                ? ["Review Docker daemon warnings with normal Docker administration tooling."]
                : null);
    }
}

internal static class DockerContainerParser
{
    public static IReadOnlyList<DockerContainerSnapshot> ParseLines(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<DockerContainerSnapshot>();
        }

        var snapshots = new List<DockerContainerSnapshot>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var name = DockerJson.GetString(root, "Names") ?? "unknown";
            var state = DockerJson.GetString(root, "State") ?? "unknown";
            var health = DockerJson.GetString(root, "HealthStatus") ?? string.Empty;
            snapshots.Add(new DockerContainerSnapshot(name, state, health));
        }

        return snapshots;
    }
}

internal static class DockerContainerEvaluator
{
    public static PluginDiagnosticResult Evaluate(
        IReadOnlyList<DockerContainerSnapshot> containers,
        DockerSettings settings)
    {
        var expected = new HashSet<string>(settings.ExpectedContainers, StringComparer.OrdinalIgnoreCase);
        var byName = containers
            .GroupBy(container => container.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var missingExpected = expected
            .Where(name => !byName.ContainsKey(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var expectedStopped = containers
            .Where(container =>
                expected.Contains(container.Name) &&
                !IsState(container, "running"))
            .ToArray();

        var unhealthy = containers
            .Where(container => IsHealth(container, "unhealthy"))
            .ToArray();

        var severeStates = containers
            .Where(container =>
                IsState(container, "dead") ||
                IsState(container, "restarting") ||
                IsState(container, "removing"))
            .ToArray();

        var paused = containers
            .Where(container => IsState(container, "paused"))
            .ToArray();

        var stopped = containers
            .Where(container =>
                IsState(container, "exited") ||
                IsState(container, "stopped"))
            .ToArray();

        var critical = missingExpected.Length > 0 ||
                       expectedStopped.Length > 0 ||
                       unhealthy.Length > 0 ||
                       severeStates.Length > 0;
        var warning = !critical &&
                      (paused.Length > 0 ||
                       (settings.WarnOnStoppedContainers && stopped.Length > 0));

        var status = critical
            ? PluginDiagnosticStatus.Critical
            : warning
                ? PluginDiagnosticStatus.Warning
                : PluginDiagnosticStatus.Healthy;

        var runningCount = containers.Count(container => IsState(container, "running"));
        var evidence = new Dictionary<string, string>
        {
            ["containerCount"] = containers.Count.ToString(CultureInfo.InvariantCulture),
            ["runningCount"] = runningCount.ToString(CultureInfo.InvariantCulture),
            ["expectedCount"] = expected.Count.ToString(CultureInfo.InvariantCulture),
            ["missingExpectedCount"] = missingExpected.Length.ToString(CultureInfo.InvariantCulture),
            ["unhealthyCount"] = unhealthy.Length.ToString(CultureInfo.InvariantCulture),
            ["severeStateCount"] = severeStates.Length.ToString(CultureInfo.InvariantCulture),
            ["pausedCount"] = paused.Length.ToString(CultureInfo.InvariantCulture),
            ["stoppedCount"] = stopped.Length.ToString(CultureInfo.InvariantCulture)
        };

        var evidenceContainers = containers
            .Where(container =>
                expected.Contains(container.Name) ||
                IsHealth(container, "unhealthy") ||
                IsState(container, "dead") ||
                IsState(container, "restarting") ||
                IsState(container, "removing") ||
                IsState(container, "paused") ||
                (settings.WarnOnStoppedContainers &&
                 (IsState(container, "exited") || IsState(container, "stopped"))))
            .OrderBy(container => container.Name, StringComparer.OrdinalIgnoreCase)
            .Take(settings.MaxContainerEvidence)
            .ToArray();

        for (var i = 0; i < evidenceContainers.Length; i++)
        {
            var container = evidenceContainers[i];
            evidence[$"container{i + 1}"] =
                $"name={container.Name}; state={Normalize(container.State)}; health={NormalizeHealth(container.HealthStatus)}";
        }

        if (missingExpected.Length > 0)
        {
            evidence["missingExpected"] = string.Join(",", missingExpected.Take(50));
        }

        var summary = status switch
        {
            PluginDiagnosticStatus.Critical =>
                $"Docker containers need attention: {missingExpected.Length} expected missing, {expectedStopped.Length} expected not running, {unhealthy.Length} unhealthy, {severeStates.Length} severe state.",
            PluginDiagnosticStatus.Warning =>
                $"Docker containers have warnings: {paused.Length} paused and {(settings.WarnOnStoppedContainers ? stopped.Length : 0)} stopped.",
            _ =>
                $"Docker containers look healthy: {runningCount}/{containers.Count} running; {expected.Count} expected container(s) satisfied."
        };

        var suggestions = status switch
        {
            PluginDiagnosticStatus.Critical =>
            [
                "Inspect the named containers with normal Docker tooling before restarting or removing anything.",
                "ERP Doctor does not start, stop, restart, remove, or recreate Docker containers automatically."
            ],
            PluginDiagnosticStatus.Warning =>
            [
                "Review paused/stopped containers and confirm whether their state is intentional."
            ],
            _ => null
        };

        return new PluginDiagnosticResult(status, summary, evidence, suggestions);
    }

    private static bool IsState(DockerContainerSnapshot container, string expected) =>
        string.Equals(Normalize(container.State), expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsHealth(DockerContainerSnapshot container, string expected) =>
        string.Equals(NormalizeHealth(container.HealthStatus), expected, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToLowerInvariant();

    private static string NormalizeHealth(string value) =>
        string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();
}
