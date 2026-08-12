using System.Reflection;
using System.Text.Json;
using ErpDoctor.Core;
using ErpDoctor.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class McpServerTests
{
    [Fact]
    public async Task DiagnosticService_SystemScopeReturnsOnlySystemChecks()
    {
        var path = WriteMinimalConfig();
        try
        {
            var service = new McpDiagnosticService(path);
            var report = await service.RunAsync(
                "system",
                TestContext.Current.CancellationToken);

            Assert.NotEmpty(report.Results);
            Assert.All(
                report.Results,
                result => Assert.Equal("system", CategoryFromCheckId(result.CheckId)));
            Assert.Empty(report.Diagnoses);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RunDiagnosticsTool_DeclaresReadOnlyAnnotations()
    {
        var method = typeof(ErpDoctorMcpTools).GetMethod(
            nameof(ErpDoctorMcpTools.RunDiagnosticsAsync),
            BindingFlags.Public | BindingFlags.Static);
        var attribute = Assert.IsType<McpServerToolAttribute>(
            Assert.Single(method!.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)));

        Assert.Equal("run_diagnostics", attribute.Name);
        Assert.True(attribute.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.True(attribute.OpenWorld);
        Assert.True(attribute.UseStructuredContent);
    }

    [Fact]
    public async Task StdioServer_ListsAndExecutesStructuredDiagnosticTool()
    {
        var configPath = WriteMinimalConfig();
        try
        {
            var serverAssembly = typeof(McpDiagnosticService).Assembly.Location;
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ERP Doctor test server",
                Command = "dotnet",
                Arguments = [serverAssembly, "--config", configPath]
            });

            await using var client = await McpClient.CreateAsync(
                transport,
                cancellationToken: TestContext.Current.CancellationToken);

            var tools = await client.ListToolsAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            var tool = Assert.Single(tools, tool => tool.Name == "run_diagnostics");
            Assert.Contains("read-only", tool.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            var result = await client.CallToolAsync(
                "run_diagnostics",
                new Dictionary<string, object?> { ["scope"] = "system" },
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(true, result.IsError);
            var structured = Assert.NotNull(result.StructuredContent);
            Assert.Equal(
                "1.0",
                structured.Value.GetProperty("schemaVersion").GetString());
            Assert.True(structured.Value.GetProperty("results").GetArrayLength() > 0);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    private static string WriteMinimalConfig()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"erp-doctor-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                system = new
                {
                    cpuWarningPercent = 100,
                    cpuCriticalPercent = 100,
                    cpuSampleMilliseconds = 100,
                    loadPerCpuWarning = 100,
                    loadPerCpuCritical = 200,
                    topProcessesLimit = 3
                }
            }));
        return path;
    }

    private static string CategoryFromCheckId(string checkId) =>
        checkId.Split('.', 2)[0];
}
