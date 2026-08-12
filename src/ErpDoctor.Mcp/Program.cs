using ErpDoctor.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

if (args.Any(arg => arg is "-h" or "--help" or "help"))
{
    Console.Error.WriteLine("Usage: erp-doctor-mcp [--config erp-doctor.json]");
    return 0;
}

string configPath;
try
{
    configPath = ParseConfigPath(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Usage: erp-doctor-mcp [--config erp-doctor.json]");
    return 2;
}

if (configPath.Contains("://", StringComparison.Ordinal))
{
    Console.Error.WriteLine("MCP configuration must be a local filesystem path; URLs are not supported.");
    return 2;
}

var fullConfigPath = Path.GetFullPath(configPath);
var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    // stdio stdout is reserved for MCP protocol frames.
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Services.AddSingleton(new McpDiagnosticService(fullConfigPath));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;

static string ParseConfigPath(string[] args)
{
    if (args.Length == 0)
    {
        return "erp-doctor.json";
    }

    if (args.Length == 2 &&
        args[0].Equals("--config", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(args[1]))
    {
        return args[1];
    }

    throw new ArgumentException(
        "Only the optional '--config <local-path>' startup argument is supported.");
}
