using System.Text.Json;

namespace ErpDoctor.Core;

public sealed class ErpDoctorOptions
{
    public SqlServerOptions SqlServer { get; init; } = new();
    public HttpOptions Http { get; init; } = new();
    public IisOptions Iis { get; init; } = new();
    public WindowsEventLogOptions WindowsEventLog { get; init; } = new();
    public PluginOptions Plugins { get; init; } = new();
    public SystemOptions System { get; init; } = new();

    public static ErpDoctorOptions Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new ErpDoctorOptions();
        }

        var json = File.ReadAllText(path);
        var options = JsonSerializer.Deserialize<ErpDoctorOptions>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? new ErpDoctorOptions();

        return options.ExpandEnvironmentVariables();
    }

    private ErpDoctorOptions ExpandEnvironmentVariables()
    {
        return new ErpDoctorOptions
        {
            SqlServer = SqlServer with
            {
                ConnectionString = EnvironmentExpander.Expand(SqlServer.ConnectionString)
            },
            Http = Http with
            {
                Endpoints = Http.Endpoints
                    .Select(endpoint => endpoint with
                    {
                        Url = EnvironmentExpander.Expand(endpoint.Url) ?? string.Empty
                    })
                    .ToArray()
            },
            Iis = Iis,
            WindowsEventLog = WindowsEventLog,
            Plugins = Plugins with
            {
                Assemblies = Plugins.Assemblies
                    .Select(path => EnvironmentExpander.Expand(path) ?? string.Empty)
                    .ToArray()
            },
            System = System
        };
    }
}

public sealed record SqlServerOptions
{
    public string? ConnectionString { get; init; }
    public double DatabaseSizeWarningGb { get; init; } = 20;
    public double LogSizeWarningGb { get; init; } = 5;
    public int BlockingWarningSeconds { get; init; } = 10;
    public int LongRunningWarningSeconds { get; init; } = 30;
    public int LargestTablesLimit { get; init; } = 10;
    public int GrowthTablesLimit { get; init; } = 50;
}

public sealed record HttpOptions
{
    public IReadOnlyList<HttpEndpointOptions> Endpoints { get; init; } =
        Array.Empty<HttpEndpointOptions>();
}

public sealed record HttpEndpointOptions
{
    public string Name { get; init; } = "HTTP endpoint";
    public string Url { get; init; } = string.Empty;
    public int ExpectedStatusCode { get; init; } = 200;
    public int TimeoutSeconds { get; init; } = 10;
    public int LatencyWarningMs { get; init; } = 1500;
}

public sealed record IisOptions
{
    public IReadOnlyList<string> AppPools { get; init; } = Array.Empty<string>();
    public IReadOnlyList<IisSiteOptions> Sites { get; init; } = Array.Empty<IisSiteOptions>();
}

public sealed record IisSiteOptions
{
    public string Name { get; init; } = "IIS site";
    public IReadOnlyList<string> ExpectedBindings { get; init; } = Array.Empty<string>();
    public bool CheckPhysicalPath { get; init; } = true;
}

public sealed record WindowsEventLogOptions
{
    public IReadOnlyList<WindowsEventLogQueryOptions> Queries { get; init; } =
        Array.Empty<WindowsEventLogQueryOptions>();
}

public sealed record WindowsEventLogQueryOptions
{
    public string Name { get; init; } = "Recent Windows errors";
    public string LogName { get; init; } = "Application";
    public int LookbackMinutes { get; init; } = 60;
    public int MaxEvents { get; init; } = 20;
    public bool IncludeWarnings { get; init; }
    public IReadOnlyList<string> Providers { get; init; } = Array.Empty<string>();
}

public sealed record PluginOptions
{
    public IReadOnlyList<string> Assemblies { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, JsonElement> Settings { get; init; } =
        new Dictionary<string, JsonElement>();
}

public sealed record SystemOptions
{
    public double DiskWarningFreePercent { get; init; } = 15;
    public double DiskCriticalFreePercent { get; init; } = 5;
    public double MemoryWarningAvailablePercent { get; init; } = 15;
}

internal static class EnvironmentExpander
{
    public static string? Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var expanded = value;

        var start = expanded.IndexOf("${", StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = expanded.IndexOf('}', start + 2);
            if (end < 0)
            {
                break;
            }

            var name = expanded[(start + 2)..end];
            var replacement = Environment.GetEnvironmentVariable(name) ?? string.Empty;
            expanded = expanded[..start] + replacement + expanded[(end + 1)..];
            start = expanded.IndexOf("${", StringComparison.Ordinal);
        }

        return expanded;
    }
}
