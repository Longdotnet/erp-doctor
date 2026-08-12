using System.Globalization;
using ErpDoctor.PluginSdk;

namespace ErpDoctor.Plugin.Redis;

internal static class RedisInfoParser
{
    public static IReadOnlyDictionary<string, string> Parse(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length > 0)
            {
                values[key] = value;
            }
        }

        return values;
    }
}

internal static class RedisPingEvaluator
{
    public static PluginDiagnosticResult Evaluate(string output)
    {
        if (string.Equals(output.Trim(), "PONG", StringComparison.OrdinalIgnoreCase))
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Healthy,
                "Redis responded to PING.");
        }

        return new PluginDiagnosticResult(
            PluginDiagnosticStatus.Error,
            "Redis PING returned an unexpected response.",
            Suggestions:
            [
                "Confirm the target endpoint is Redis-compatible and the configured credentials are correct."
            ]);
    }
}

internal static class RedisServerEvaluator
{
    public static PluginDiagnosticResult Evaluate(IReadOnlyDictionary<string, string> info)
    {
        if (!info.TryGetValue("redis_version", out var version) || string.IsNullOrWhiteSpace(version))
        {
            return UnexpectedInfo("server");
        }

        var evidence = new Dictionary<string, string>
        {
            ["redisVersion"] = version
        };

        if (info.TryGetValue("redis_mode", out var mode) && !string.IsNullOrWhiteSpace(mode))
        {
            evidence["redisMode"] = mode;
        }

        if (TryInt64(info, "uptime_in_seconds", out var uptimeSeconds))
        {
            evidence["uptimeHours"] = (uptimeSeconds / 3600d).ToString("F2", CultureInfo.InvariantCulture);
        }

        return new PluginDiagnosticResult(
            PluginDiagnosticStatus.Healthy,
            $"Redis {version} is reachable and returned server metadata.",
            evidence);
    }

    private static PluginDiagnosticResult UnexpectedInfo(string section) =>
        new(
            PluginDiagnosticStatus.Error,
            $"Redis INFO {section} returned unexpected output.",
            Suggestions:
            [
                "Confirm the target supports the standard Redis INFO response format."
            ]);

    internal static bool TryInt64(
        IReadOnlyDictionary<string, string> info,
        string key,
        out long value)
    {
        value = 0;
        return info.TryGetValue(key, out var raw) &&
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    internal static bool TryDouble(
        IReadOnlyDictionary<string, string> info,
        string key,
        out double value)
    {
        value = 0;
        return info.TryGetValue(key, out var raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

internal static class RedisMemoryEvaluator
{
    public static PluginDiagnosticResult Evaluate(
        IReadOnlyDictionary<string, string> info,
        RedisSettings settings)
    {
        if (!RedisServerEvaluator.TryInt64(info, "used_memory", out var usedMemory))
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Error,
                "Redis INFO memory did not include a valid used_memory value.");
        }

        var evidence = new Dictionary<string, string>
        {
            ["usedMemoryMb"] = ToMegabytes(usedMemory)
        };

        if (RedisServerEvaluator.TryInt64(info, "used_memory_peak", out var peakMemory))
        {
            evidence["usedMemoryPeakMb"] = ToMegabytes(peakMemory);
        }

        if (RedisServerEvaluator.TryDouble(info, "mem_fragmentation_ratio", out var fragmentation))
        {
            evidence["fragmentationRatio"] = fragmentation.ToString("F2", CultureInfo.InvariantCulture);
        }

        if (!RedisServerEvaluator.TryInt64(info, "maxmemory", out var maxMemory) || maxMemory <= 0)
        {
            evidence["maxMemoryMb"] = "unlimited";
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Info,
                $"Redis uses {ToMegabytes(usedMemory)} MB and has no maxmemory limit configured.",
                evidence,
                [
                    "Confirm host-level memory capacity is intentional for this Redis workload."
                ]);
        }

        evidence["maxMemoryMb"] = ToMegabytes(maxMemory);
        var usedPercent = (double)usedMemory / maxMemory * 100d;
        evidence["usedMemoryPercent"] = usedPercent.ToString("F2", CultureInfo.InvariantCulture);

        if (usedPercent >= settings.MemoryCriticalPercent)
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Critical,
                $"Redis memory usage is {usedPercent:F1}% of maxmemory.",
                evidence,
                [
                    "Review eviction policy, application cache growth, and available memory before the instance reaches its limit."
                ]);
        }

        if (usedPercent >= settings.MemoryWarningPercent)
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Warning,
                $"Redis memory usage is {usedPercent:F1}% of maxmemory.",
                evidence,
                [
                    "Review memory growth and eviction behavior before usage reaches the critical threshold."
                ]);
        }

        return new PluginDiagnosticResult(
            PluginDiagnosticStatus.Healthy,
            $"Redis memory usage is {usedPercent:F1}% of maxmemory.",
            evidence);
    }

    private static string ToMegabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("F2", CultureInfo.InvariantCulture);
}

internal static class RedisPersistenceEvaluator
{
    public static PluginDiagnosticResult Evaluate(IReadOnlyDictionary<string, string> info)
    {
        var evidence = new Dictionary<string, string>();
        var loading = RedisServerEvaluator.TryInt64(info, "loading", out var loadingValue) && loadingValue != 0;
        evidence["loading"] = loading ? "true" : "false";

        info.TryGetValue("rdb_last_bgsave_status", out var rdbStatus);
        if (!string.IsNullOrWhiteSpace(rdbStatus))
        {
            evidence["rdbLastBgsaveStatus"] = rdbStatus;
        }

        var aofEnabled = RedisServerEvaluator.TryInt64(info, "aof_enabled", out var aofEnabledValue) && aofEnabledValue != 0;
        evidence["aofEnabled"] = aofEnabled ? "true" : "false";

        info.TryGetValue("aof_last_bgrewrite_status", out var aofStatus);
        if (aofEnabled && !string.IsNullOrWhiteSpace(aofStatus))
        {
            evidence["aofLastBgrewriteStatus"] = aofStatus;
        }

        if (string.Equals(rdbStatus, "err", StringComparison.OrdinalIgnoreCase) ||
            (aofEnabled && string.Equals(aofStatus, "err", StringComparison.OrdinalIgnoreCase)))
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Critical,
                "Redis reports a failed background persistence operation.",
                evidence,
                [
                    "Inspect Redis server logs, disk capacity, filesystem permissions, and persistence configuration."
                ]);
        }

        if (loading)
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Warning,
                "Redis is currently loading a dataset.",
                evidence,
                [
                    "Re-run the diagnostic after loading completes and investigate if startup loading is unexpectedly long."
                ]);
        }

        return new PluginDiagnosticResult(
            PluginDiagnosticStatus.Healthy,
            "Redis persistence state reports no active loading or last-operation failure.",
            evidence);
    }
}

internal static class RedisReplicationEvaluator
{
    public static PluginDiagnosticResult Evaluate(
        IReadOnlyDictionary<string, string> info,
        RedisSettings settings)
    {
        if (!info.TryGetValue("role", out var role) || string.IsNullOrWhiteSpace(role))
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Error,
                "Redis INFO replication did not include a role.");
        }

        var normalizedRole = role.Trim().ToLowerInvariant();
        var evidence = new Dictionary<string, string>
        {
            ["role"] = normalizedRole
        };

        if (normalizedRole == "master")
        {
            if (RedisServerEvaluator.TryInt64(info, "connected_slaves", out var connectedReplicas))
            {
                evidence["connectedReplicas"] = connectedReplicas.ToString(CultureInfo.InvariantCulture);
            }

            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Healthy,
                "Redis is operating as a primary node.",
                evidence);
        }

        if (normalizedRole is not ("slave" or "replica"))
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Info,
                $"Redis replication role is '{normalizedRole}'.",
                evidence);
        }

        info.TryGetValue("master_link_status", out var linkStatus);
        if (!string.IsNullOrWhiteSpace(linkStatus))
        {
            evidence["primaryLinkStatus"] = linkStatus;
        }

        var syncInProgress = RedisServerEvaluator.TryInt64(info, "master_sync_in_progress", out var syncValue) && syncValue != 0;
        evidence["syncInProgress"] = syncInProgress ? "true" : "false";

        var hasLag = RedisServerEvaluator.TryInt64(info, "master_last_io_seconds_ago", out var lagSeconds);
        if (hasLag)
        {
            evidence["lastPrimaryIoSecondsAgo"] = lagSeconds.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.Equals(linkStatus, "up", StringComparison.OrdinalIgnoreCase))
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Critical,
                "Redis replica link to its primary is down.",
                evidence,
                [
                    "Check network reachability, primary availability, TLS/authentication, and replication configuration."
                ]);
        }

        if (syncInProgress)
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Warning,
                "Redis replica is synchronizing from its primary.",
                evidence,
                [
                    "Confirm synchronization completes and re-run the diagnostic if the state persists."
                ]);
        }

        if (hasLag && lagSeconds >= settings.ReplicaLagCriticalSeconds)
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Critical,
                $"Redis replica has not received primary I/O for {lagSeconds}s.",
                evidence,
                [
                    "Inspect primary load, network latency, and replication health."
                ]);
        }

        if (hasLag && lagSeconds >= settings.ReplicaLagWarningSeconds)
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Warning,
                $"Redis replica has not received primary I/O for {lagSeconds}s.",
                evidence,
                [
                    "Watch replication latency and investigate if lag continues to increase."
                ]);
        }

        return new PluginDiagnosticResult(
            PluginDiagnosticStatus.Healthy,
            "Redis replica link is up and replication lag is within the configured threshold.",
            evidence);
    }
}
