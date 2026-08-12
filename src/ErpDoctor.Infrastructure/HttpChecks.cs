using System.Diagnostics;
using ErpDoctor.Core;

namespace ErpDoctor.Infrastructure.HttpDiagnostics;

public sealed class HttpEndpointCheck(HttpEndpointOptions endpoint) : IDiagnosticCheck
{
    public string Id => $"http.{Normalize(endpoint.Name)}";
    public string Name => endpoint.Name;
    public string Category => "http";

    public async Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Url))
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Skipped,
                "No URL configured.");
        }

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, endpoint.TimeoutSeconds))
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await client.GetAsync(endpoint.Url, cancellationToken);
            stopwatch.Stop();

            var code = (int)response.StatusCode;
            var status = code != endpoint.ExpectedStatusCode
                ? DiagnosticStatus.Critical
                : stopwatch.ElapsedMilliseconds >= endpoint.LatencyWarningMs
                    ? DiagnosticStatus.Warning
                    : DiagnosticStatus.Healthy;

            return new DiagnosticResult(
                Id,
                Name,
                status,
                $"HTTP {code} in {stopwatch.ElapsedMilliseconds} ms",
                new Dictionary<string, string>
                {
                    ["url"] = endpoint.Url,
                    ["statusCode"] = code.ToString(),
                    ["latencyMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                    ["expectedStatusCode"] = endpoint.ExpectedStatusCode.ToString()
                },
                status == DiagnosticStatus.Critical
                    ? ["Check the application process, reverse proxy/IIS, port binding, startup logs, and database dependency."]
                    : status == DiagnosticStatus.Warning
                        ? ["Endpoint is healthy but slow; correlate with SQL blocking and server resource checks."]
                        : null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Critical,
                $"Timed out after {stopwatch.ElapsedMilliseconds} ms",
                new Dictionary<string, string>
                {
                    ["url"] = endpoint.Url,
                    ["latencyMs"] = stopwatch.ElapsedMilliseconds.ToString()
                },
                ["Check application health, network path, IIS bindings, and downstream database dependencies."]);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Critical,
                $"Request failed: {ex.Message}",
                new Dictionary<string, string>
                {
                    ["url"] = endpoint.Url,
                    ["latencyMs"] = stopwatch.ElapsedMilliseconds.ToString()
                },
                ["Confirm DNS, TCP port, TLS certificate, IIS/site state, and application startup."]);
        }
    }

    private static string Normalize(string value) =>
        new string(value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray())
            .Trim('-');
}
