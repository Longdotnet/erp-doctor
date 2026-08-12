using System.Text.Json;

namespace ErpDoctor.Core;

public sealed class ErpDoctorOptions
{
    public SqlServerOptions SqlServer { get; init; } = new();
    public HttpOptions Http { get; init; } = new();
    public IisOptions Iis { get; init; } = new();
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
