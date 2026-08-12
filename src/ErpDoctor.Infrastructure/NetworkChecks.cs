using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ErpDoctor.Core;

namespace ErpDoctor.Infrastructure.NetworkDiagnostics;

public sealed class DnsResolutionCheck(NetworkTargetOptions target) : IDiagnosticCheck
{
    public string Id => $"network.dns.{NetworkCheckNames.Normalize(target.Name)}";
    public string Name => $"{target.Name} DNS";
    public string Category => "network";

    public async Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        if (string.IsNullOrWhiteSpace(target.Host))
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Skipped,
                "No host configured.");
        }

        var timeoutSeconds = Math.Clamp(target.TimeoutSeconds, 1, 60);
        var warningMs = Math.Max(1, target.LatencyWarningMs);
        var addressLimit = Math.Clamp(target.MaxResolvedAddresses, 1, 20);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target.Host, timeoutCts.Token);
            stopwatch.Stop();

            if (addresses.Length == 0)
            {
                return new DiagnosticResult(
                    Id,
                    Name,
                    DiagnosticStatus.Critical,
                    $"DNS returned no addresses for {target.Host}.",
                    BuildEvidence(stopwatch.ElapsedMilliseconds),
                    ["Confirm the hostname, DNS suffix/search configuration, and DNS server reachability."]);
            }

            var status = stopwatch.ElapsedMilliseconds >= warningMs
                ? DiagnosticStatus.Warning
                : DiagnosticStatus.Healthy;
            var evidence = BuildEvidence(stopwatch.ElapsedMilliseconds);
            evidence["addressCount"] = addresses.Length.ToString(CultureInfo.InvariantCulture);
            evidence["resolvedAddresses"] = string.Join(
                ", ",
                addresses.Take(addressLimit).Select(address => address.ToString()));
            evidence["addressesTruncated"] = addresses.Length > addressLimit ? "true" : "false";

            return new DiagnosticResult(
                Id,
                Name,
                status,
                $"Resolved {target.Host} to {addresses.Length} address(es) in {stopwatch.ElapsedMilliseconds} ms.",
                evidence,
                status == DiagnosticStatus.Warning
                    ? ["DNS resolved successfully but slowly; check DNS server latency and network path before blaming the application."]
                    : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Critical,
                $"DNS resolution timed out after {stopwatch.ElapsedMilliseconds} ms.",
                BuildEvidence(stopwatch.ElapsedMilliseconds),
                ["Confirm DNS server reachability, hostname configuration, and network path."]);
        }
        catch (SocketException ex)
        {
            stopwatch.Stop();
            var evidence = BuildEvidence(stopwatch.ElapsedMilliseconds);
            evidence["socketError"] = ex.SocketErrorCode.ToString();
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Critical,
                $"DNS resolution failed for {target.Host} ({ex.SocketErrorCode}).",
                evidence,
                ["Confirm the hostname exists and the configured DNS servers are reachable from this machine."]);
        }
        catch (ArgumentException)
        {
            stopwatch.Stop();
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                "Configured network host is not a valid DNS hostname or IP address.",
                BuildEvidence(stopwatch.ElapsedMilliseconds));
        }
    }

    private Dictionary<string, string> BuildEvidence(long latencyMs) =>
        new()
        {
            ["host"] = target.Host,
            ["latencyMs"] = latencyMs.ToString(CultureInfo.InvariantCulture)
        };
}

public sealed class TcpConnectivityCheck(NetworkTargetOptions target) : IDiagnosticCheck
{
    public string Id => $"network.tcp.{NetworkCheckNames.Normalize(target.Name)}";
    public string Name => $"{target.Name} TCP";
    public string Category => "network";

    public async Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        if (string.IsNullOrWhiteSpace(target.Host))
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Skipped,
                "No host configured.");
        }

        if (target.Port is < 1 or > IPEndPoint.MaxPort)
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                $"Configured TCP port {target.Port} is outside the valid range 1-65535.",
                new Dictionary<string, string>
                {
                    ["host"] = target.Host,
                    ["port"] = target.Port.ToString(CultureInfo.InvariantCulture)
                });
        }

        var timeoutSeconds = Math.Clamp(target.TimeoutSeconds, 1, 60);
        var warningMs = Math.Max(1, target.LatencyWarningMs);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var client = new TcpClient();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await client.ConnectAsync(target.Host, target.Port, timeoutCts.Token);
            stopwatch.Stop();

            var status = stopwatch.ElapsedMilliseconds >= warningMs
                ? DiagnosticStatus.Warning
                : DiagnosticStatus.Healthy;
            var evidence = BuildEvidence(stopwatch.ElapsedMilliseconds);
            if (client.Client.RemoteEndPoint is IPEndPoint remote)
            {
                var address = remote.Address.IsIPv4MappedToIPv6
                    ? remote.Address.MapToIPv4()
                    : remote.Address;
                evidence["remoteAddress"] = address.ToString();
            }

            return new DiagnosticResult(
                Id,
                Name,
                status,
                $"Connected to {target.Host}:{target.Port} in {stopwatch.ElapsedMilliseconds} ms.",
                evidence,
                status == DiagnosticStatus.Warning
                    ? ["The TCP port is reachable but slow; correlate with DNS latency, routing/VPN, firewall inspection, and server load."]
                    : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Critical,
                $"TCP connection timed out after {stopwatch.ElapsedMilliseconds} ms.",
                BuildEvidence(stopwatch.ElapsedMilliseconds),
                ["Confirm the destination service is listening and check firewall, routing, VPN, security-group, and NAT rules."]);
        }
        catch (SocketException ex)
        {
            stopwatch.Stop();
            var evidence = BuildEvidence(stopwatch.ElapsedMilliseconds);
            evidence["socketError"] = ex.SocketErrorCode.ToString();
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Critical,
                $"TCP connection to {target.Host}:{target.Port} failed ({ex.SocketErrorCode}).",
                evidence,
                ["Confirm the service is listening on the expected interface/port and inspect firewall/routing rules before restarting the application."]);
        }
        catch (ArgumentException)
        {
            stopwatch.Stop();
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                "Configured network host or port is invalid.",
                BuildEvidence(stopwatch.ElapsedMilliseconds));
        }
    }

    private Dictionary<string, string> BuildEvidence(long latencyMs) =>
        new()
        {
            ["host"] = target.Host,
            ["port"] = target.Port.ToString(CultureInfo.InvariantCulture),
            ["latencyMs"] = latencyMs.ToString(CultureInfo.InvariantCulture)
        };
}

internal static class NetworkCheckNames
{
    public static string Normalize(string value)
    {
        var normalized = new string(value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray())
            .Trim('-');
        return normalized.Length == 0 ? "target" : normalized;
    }
}
