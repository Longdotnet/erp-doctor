using System.Globalization;
using System.Text.Json;
using ErpDoctor.PluginSdk;

namespace ErpDoctor.Plugin.RabbitMq;

internal static class RabbitMqOverviewEvaluator
{
    public static PluginDiagnosticResult Evaluate(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("overview");
            }

            var version = ReadString(root, "rabbitmq_version");
            if (string.IsNullOrWhiteSpace(version))
            {
                return Invalid("overview");
            }

            var evidence = new Dictionary<string, string>
            {
                ["rabbitMqVersion"] = version
            };

            AddString(root, "management_version", evidence, "managementVersion");
            AddString(root, "cluster_name", evidence, "clusterName");

            if (root.TryGetProperty("object_totals", out var objectTotals) &&
                objectTotals.ValueKind == JsonValueKind.Object)
            {
                AddInt64(objectTotals, "connections", evidence, "connections");
                AddInt64(objectTotals, "channels", evidence, "channels");
                AddInt64(objectTotals, "exchanges", evidence, "exchanges");
                AddInt64(objectTotals, "queues", evidence, "queues");
                AddInt64(objectTotals, "consumers", evidence, "consumers");
            }

            if (root.TryGetProperty("queue_totals", out var queueTotals) &&
                queueTotals.ValueKind == JsonValueKind.Object)
            {
                AddInt64(queueTotals, "messages", evidence, "messages");
                AddInt64(queueTotals, "messages_ready", evidence, "messagesReady");
                AddInt64(queueTotals, "messages_unacknowledged", evidence, "messagesUnacknowledged");
            }

            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Healthy,
                $"RabbitMQ {version} management API is reachable.",
                evidence);
        }
        catch (JsonException)
        {
            return Invalid("overview");
        }
    }

    private static PluginDiagnosticResult Invalid(string endpoint) =>
        new(
            PluginDiagnosticStatus.Error,
            $"RabbitMQ {endpoint} endpoint returned unexpected JSON.",
            Suggestions:
            [
                "Confirm baseUrl points to the RabbitMQ management HTTP API and the endpoint is not being rewritten by a proxy."
            ]);

    internal static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    internal static bool TryReadInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value);
    }

    internal static bool TryReadBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            (value = property.GetBoolean()) == value;
    }

    private static void AddString(
        JsonElement source,
        string sourceName,
        IDictionary<string, string> evidence,
        string evidenceName)
    {
        var value = ReadString(source, sourceName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            evidence[evidenceName] = value;
        }
    }

    private static void AddInt64(
        JsonElement source,
        string sourceName,
        IDictionary<string, string> evidence,
        string evidenceName)
    {
        if (TryReadInt64(source, sourceName, out var value))
        {
            evidence[evidenceName] = value.ToString(CultureInfo.InvariantCulture);
        }
    }
}

internal static class RabbitMqNodeEvaluator
{
    public static PluginDiagnosticResult Evaluate(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Invalid();
            }

            var nodeCount = 0;
            var runningCount = 0;
            var memoryAlarms = 0;
            var diskAlarms = 0;
            var partitionedNodes = 0;
            var downNodes = 0;
            var affectedNodes = new List<string>();

            foreach (var node in document.RootElement.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                nodeCount++;
                var name = RabbitMqOverviewEvaluator.ReadString(node, "name") ?? $"node-{nodeCount}";

                var running = !RabbitMqOverviewEvaluator.TryReadBoolean(node, "running", out var runningValue) || runningValue;
                if (running)
                {
                    runningCount++;
                }
                else
                {
                    downNodes++;
                    AddAffected(affectedNodes, name, "down");
                }

                if (RabbitMqOverviewEvaluator.TryReadBoolean(node, "mem_alarm", out var memAlarm) && memAlarm)
                {
                    memoryAlarms++;
                    AddAffected(affectedNodes, name, "memory-alarm");
                }

                if (RabbitMqOverviewEvaluator.TryReadBoolean(node, "disk_free_alarm", out var diskAlarm) && diskAlarm)
                {
                    diskAlarms++;
                    AddAffected(affectedNodes, name, "disk-alarm");
                }

                if (node.TryGetProperty("partitions", out var partitions) &&
                    partitions.ValueKind == JsonValueKind.Array &&
                    partitions.GetArrayLength() > 0)
                {
                    partitionedNodes++;
                    AddAffected(affectedNodes, name, "partition");
                }
            }

            if (nodeCount == 0)
            {
                return new PluginDiagnosticResult(
                    PluginDiagnosticStatus.Error,
                    "RabbitMQ management API returned no cluster nodes.");
            }

            var evidence = new Dictionary<string, string>
            {
                ["nodeCount"] = nodeCount.ToString(CultureInfo.InvariantCulture),
                ["runningNodes"] = runningCount.ToString(CultureInfo.InvariantCulture),
                ["downNodes"] = downNodes.ToString(CultureInfo.InvariantCulture),
                ["memoryAlarmNodes"] = memoryAlarms.ToString(CultureInfo.InvariantCulture),
                ["diskAlarmNodes"] = diskAlarms.ToString(CultureInfo.InvariantCulture),
                ["partitionedNodes"] = partitionedNodes.ToString(CultureInfo.InvariantCulture)
            };

            if (affectedNodes.Count > 0)
            {
                evidence["affectedNodes"] = string.Join(", ", affectedNodes.Take(10));
            }

            if (downNodes > 0 || memoryAlarms > 0 || diskAlarms > 0 || partitionedNodes > 0)
            {
                return new PluginDiagnosticResult(
                    PluginDiagnosticStatus.Critical,
                    $"RabbitMQ cluster reports {downNodes} down node(s), {memoryAlarms} memory alarm(s), {diskAlarms} disk alarm(s), and {partitionedNodes} partitioned node(s).",
                    evidence,
                    [
                        "Inspect the affected RabbitMQ nodes and resolve broker-native resource alarms or network partitions before increasing application load.",
                        "Do not clear alarms by deleting messages automatically; identify the underlying disk, memory, or cluster connectivity cause first."
                    ]);
            }

            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Healthy,
                $"RabbitMQ reports {nodeCount} running node(s) with no memory/disk alarms or network partitions.",
                evidence);
        }
        catch (JsonException)
        {
            return Invalid();
        }
    }

    private static void AddAffected(ICollection<string> affected, string node, string reason)
    {
        if (affected.Count < 20)
        {
            affected.Add($"{node} ({reason})");
        }
    }

    private static PluginDiagnosticResult Invalid() =>
        new(
            PluginDiagnosticStatus.Error,
            "RabbitMQ nodes endpoint returned unexpected JSON.");
}

internal sealed record RabbitMqQueueSnapshot(
    string Name,
    string VirtualHost,
    long Ready,
    long Unacknowledged,
    long? Consumers);

internal static class RabbitMqQueueEvaluator
{
    public static PluginDiagnosticResult Evaluate(string json, RabbitMqSettings settings)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var (items, totalCount) = ResolveItems(document.RootElement);
            if (items is null)
            {
                return Invalid();
            }

            var queues = new List<RabbitMqQueueSnapshot>();
            foreach (var queue in items.Value.EnumerateArray())
            {
                if (queue.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = RabbitMqOverviewEvaluator.ReadString(queue, "name") ?? "<unnamed>";
                var vhost = RabbitMqOverviewEvaluator.ReadString(queue, "vhost") ?? settings.VirtualHost ?? "<unknown>";
                RabbitMqOverviewEvaluator.TryReadInt64(queue, "messages_ready", out var ready);
                RabbitMqOverviewEvaluator.TryReadInt64(queue, "messages_unacknowledged", out var unacked);
                long? consumers = RabbitMqOverviewEvaluator.TryReadInt64(queue, "consumers", out var consumerCount)
                    ? consumerCount
                    : null;

                queues.Add(new RabbitMqQueueSnapshot(name, vhost, ready, unacked, consumers));
            }

            var issues = queues
                .Select(queue => EvaluateQueue(queue, settings))
                .Where(issue => issue.Severity > 0)
                .OrderByDescending(issue => issue.Severity)
                .ThenByDescending(issue => issue.Score)
                .ToList();

            var criticalCount = issues.Count(issue => issue.Severity == 2);
            var warningCount = issues.Count(issue => issue.Severity == 1);
            var noConsumerCount = queues.Count(queue =>
                queue.Consumers == 0 && queue.Ready > 0);

            var evidence = new Dictionary<string, string>
            {
                ["inspectedQueues"] = queues.Count.ToString(CultureInfo.InvariantCulture),
                ["scanLimit"] = settings.MaxQueues.ToString(CultureInfo.InvariantCulture),
                ["criticalQueues"] = criticalCount.ToString(CultureInfo.InvariantCulture),
                ["warningQueues"] = warningCount.ToString(CultureInfo.InvariantCulture),
                ["queuesWithReadyMessagesAndNoConsumers"] = noConsumerCount.ToString(CultureInfo.InvariantCulture)
            };

            if (totalCount is not null)
            {
                evidence["totalQueues"] = totalCount.Value.ToString(CultureInfo.InvariantCulture);
                evidence["scanTruncated"] = totalCount.Value > queues.Count ? "true" : "false";
            }

            if (issues.Count > 0)
            {
                evidence["queueIssues"] = string.Join(
                    "; ",
                    issues.Take(settings.MaxQueueEvidence).Select(issue => issue.Render()));
            }

            var suggestions = new List<string>();
            if (totalCount is > 0 && totalCount > queues.Count)
            {
                suggestions.Add(
                    "Only the configured first queue page was inspected. Scope the provider to a virtual host or raise maxQueues (up to 500) if broader coverage is required.");
            }

            if (criticalCount > 0)
            {
                suggestions.Add(
                    "Inspect producers/consumers and queue-specific rates before purging, deleting, or replaying messages.");
                return new PluginDiagnosticResult(
                    PluginDiagnosticStatus.Critical,
                    $"RabbitMQ queue scan found {criticalCount} critical and {warningCount} warning queue condition(s).",
                    evidence,
                    suggestions);
            }

            if (warningCount > 0)
            {
                suggestions.Add(
                    "Inspect queue growth, consumer availability, acknowledgement latency, and downstream dependency health.");
                return new PluginDiagnosticResult(
                    PluginDiagnosticStatus.Warning,
                    $"RabbitMQ queue scan found {warningCount} warning queue condition(s).",
                    evidence,
                    suggestions);
            }

            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Healthy,
                $"RabbitMQ inspected {queues.Count} queue(s) with no configured backlog/unacknowledged threshold breach.",
                evidence,
                suggestions);
        }
        catch (JsonException)
        {
            return Invalid();
        }
    }

    private static RabbitMqQueueIssue EvaluateQueue(
        RabbitMqQueueSnapshot queue,
        RabbitMqSettings settings)
    {
        var severity = 0;
        var reasons = new List<string>();

        if (queue.Ready >= settings.ReadyMessagesCritical)
        {
            severity = 2;
            reasons.Add($"ready={queue.Ready}");
        }
        else if (queue.Ready >= settings.ReadyMessagesWarning)
        {
            severity = Math.Max(severity, 1);
            reasons.Add($"ready={queue.Ready}");
        }

        if (queue.Unacknowledged >= settings.UnackedMessagesCritical)
        {
            severity = 2;
            reasons.Add($"unacked={queue.Unacknowledged}");
        }
        else if (queue.Unacknowledged >= settings.UnackedMessagesWarning)
        {
            severity = Math.Max(severity, 1);
            reasons.Add($"unacked={queue.Unacknowledged}");
        }

        if (settings.WarnOnNoConsumersWithReadyMessages &&
            queue.Consumers == 0 &&
            queue.Ready > 0)
        {
            severity = Math.Max(severity, 1);
            reasons.Add("consumers=0");
        }

        return new RabbitMqQueueIssue(
            queue,
            severity,
            queue.Ready + queue.Unacknowledged,
            reasons);
    }

    private static (JsonElement? Items, long? TotalCount) ResolveItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return (root, null);
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        long? totalCount = RabbitMqOverviewEvaluator.TryReadInt64(root, "total_count", out var count)
            ? count
            : null;
        return (items, totalCount);
    }

    private static PluginDiagnosticResult Invalid() =>
        new(
            PluginDiagnosticStatus.Error,
            "RabbitMQ queues endpoint returned unexpected JSON.");

    private sealed record RabbitMqQueueIssue(
        RabbitMqQueueSnapshot Queue,
        int Severity,
        long Score,
        IReadOnlyList<string> Reasons)
    {
        public string Render()
        {
            var consumerText = Queue.Consumers is null
                ? "consumers=n/a"
                : $"consumers={Queue.Consumers.Value}";
            var reasonText = Reasons.Count == 0 ? "threshold" : string.Join(",", Reasons);
            return $"{Queue.VirtualHost}/{Queue.Name} [{reasonText},{consumerText}]";
        }
    }
}
