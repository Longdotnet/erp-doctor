using System.Net;
using System.Net.Sockets;
using ErpDoctor.Core;
using ErpDoctor.Infrastructure.NetworkDiagnostics;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class NetworkDiagnosticsTests
{
    [Fact]
    public async Task DnsResolution_LocalhostIsHealthyAndEvidenceIsBounded()
    {
        var target = new NetworkTargetOptions
        {
            Name = "Local API",
            Host = "localhost",
            TimeoutSeconds = 5,
            LatencyWarningMs = 10_000,
            MaxResolvedAddresses = 1
        };
        var check = new DnsResolutionCheck(target);

        var result = await check.ExecuteAsync(
            new DiagnosticContext(new ErpDoctorOptions()),
            TestContext.Current.CancellationToken);

        Assert.Equal(DiagnosticStatus.Healthy, result.Status);
        Assert.Equal("localhost", result.EvidenceOrEmpty["host"]);
        Assert.True(int.Parse(result.EvidenceOrEmpty["addressCount"]) >= 1);
        Assert.False(string.IsNullOrWhiteSpace(result.EvidenceOrEmpty["resolvedAddresses"]));
    }

    [Fact]
    public async Task TcpConnectivity_LoopbackListenerIsHealthy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var check = new TcpConnectivityCheck(new NetworkTargetOptions
            {
                Name = "Loopback service",
                Host = "127.0.0.1",
                Port = port,
                TimeoutSeconds = 5,
                LatencyWarningMs = 10_000
            });

            var result = await check.ExecuteAsync(
                new DiagnosticContext(new ErpDoctorOptions()),
                TestContext.Current.CancellationToken);
            using var accepted = await acceptTask;

            Assert.Equal(DiagnosticStatus.Healthy, result.Status);
            Assert.Equal("127.0.0.1", result.EvidenceOrEmpty["host"]);
            Assert.Equal(port.ToString(), result.EvidenceOrEmpty["port"]);
            Assert.Equal("127.0.0.1", result.EvidenceOrEmpty["remoteAddress"]);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task TcpConnectivity_InvalidPortReturnsConfigError(int port)
    {
        var check = new TcpConnectivityCheck(new NetworkTargetOptions
        {
            Name = "Invalid",
            Host = "localhost",
            Port = port
        });

        var result = await check.ExecuteAsync(
            new DiagnosticContext(new ErpDoctorOptions()),
            TestContext.Current.CancellationToken);

        Assert.Equal(DiagnosticStatus.Error, result.Status);
        Assert.Contains("1-65535", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyHostSkipsDnsAndTcpWithoutNetworkAccess()
    {
        var target = new NetworkTargetOptions
        {
            Name = "Not configured",
            Host = string.Empty,
            Port = 443
        };
        var context = new DiagnosticContext(new ErpDoctorOptions());

        var dns = await new DnsResolutionCheck(target).ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);
        var tcp = await new TcpConnectivityCheck(target).ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(DiagnosticStatus.Skipped, dns.Status);
        Assert.Equal(DiagnosticStatus.Skipped, tcp.Status);
    }

    [Fact]
    public void Options_LoadExpandsNetworkHostEnvironmentVariable()
    {
        var variableName = $"ERP_DOCTOR_NETWORK_HOST_{Guid.NewGuid():N}";
        var path = Path.Combine(
            Path.GetTempPath(),
            $"erp-doctor-network-{Guid.NewGuid():N}.json");
        Environment.SetEnvironmentVariable(variableName, "db.internal.example");

        try
        {
            File.WriteAllText(
                path,
                $$"""
                {
                  "network": {
                    "targets": [
                      {
                        "name": "ERP DB",
                        "host": "${{{variableName}}}",
                        "port": 1433
                      }
                    ]
                  }
                }
                """);

            var options = ErpDoctorOptions.Load(path);
            var target = Assert.Single(options.Network.Targets);

            Assert.Equal("db.internal.example", target.Host);
            Assert.Equal(1433, target.Port);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
