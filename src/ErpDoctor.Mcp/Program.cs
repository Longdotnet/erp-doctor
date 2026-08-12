using ErpDoctor.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var configPath = GetConfigPath(args) ?? "erp-doctor.json";
if (configPath.Contains("://", StringComparison.Ordinal))
{
    Console.Error.WriteLine("MCP configuration must be a local filesystem path; URLs are not supported.");
    return 2;
}

var fullConfigPath = Path.GetFullPath(configPath);
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Services.AddSingleton(new McpDiagnosticService(fullConfigPath));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;

static string? GetConfigPath(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "-h" or "--help" or "help")
        {
            Console.Error.WriteLine("Usage: erp-doctor-mcp [--config erp-doctor.json]");
            Environment.ExitCode = 0;
            return null;
        }

        if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                throw new ArgumentException("--config requires a local file path.");
            }

            return args[i + 1];
        }
    }

    return null;
}
