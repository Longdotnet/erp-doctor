using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ErpDoctor.Plugin.RabbitMq;

internal sealed record RabbitMqApiResult(
    bool Succeeded,
    HttpStatusCode? StatusCode,
    string Body,
    string FailureSummary)
{
    public static RabbitMqApiResult Success(HttpStatusCode statusCode, string body) =>
        new(true, statusCode, body, string.Empty);

    public static RabbitMqApiResult Failure(string summary, HttpStatusCode? statusCode = null) =>
        new(false, statusCode, string.Empty, summary);
}

internal sealed class RabbitMqApiClient
{
    private const int MaxResponseBytes = 2_000_000;

    public async Task<RabbitMqApiResult> GetAsync(
        RabbitMqSettings settings,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var target = ResolveTarget(settings, relativePath);
        if (!target.Succeeded)
        {
            return RabbitMqApiResult.Failure(target.FailureSummary);
        }

        var password = Environment.GetEnvironmentVariable(settings.PasswordEnvironmentVariable);
        if (string.IsNullOrEmpty(password))
        {
            return RabbitMqApiResult.Failure(
                $"RabbitMQ password environment variable '{settings.PasswordEnvironmentVariable}' is not set.");
        }

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, target.Uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            CreateBasicAuthParameter(settings.Username, password));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RabbitMqApiResult.Failure(
                $"RabbitMQ management API request exceeded the {settings.RequestTimeoutSeconds}s timeout.");
        }
        catch (HttpRequestException ex)
        {
            return RabbitMqApiResult.Failure(
                $"RabbitMQ management API request failed ({ex.GetType().Name}).");
        }
        catch (InvalidOperationException ex)
        {
            return RabbitMqApiResult.Failure(
                $"RabbitMQ management API request could not be created ({ex.GetType().Name}).");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return RabbitMqApiResult.Failure(
                    ClassifyStatus(response.StatusCode),
                    response.StatusCode);
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > MaxResponseBytes)
            {
                return RabbitMqApiResult.Failure(
                    "RabbitMQ management API response exceeded the 2,000,000-byte safety limit.",
                    response.StatusCode);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
                var body = await ReadBoundedAsync(stream, MaxResponseBytes, timeoutCts.Token);
                return body is null
                    ? RabbitMqApiResult.Failure(
                        "RabbitMQ management API response exceeded the 2,000,000-byte safety limit.",
                        response.StatusCode)
                    : RabbitMqApiResult.Success(response.StatusCode, body);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return RabbitMqApiResult.Failure(
                    $"RabbitMQ management API response exceeded the {settings.RequestTimeoutSeconds}s timeout.",
                    response.StatusCode);
            }
            catch (IOException ex)
            {
                return RabbitMqApiResult.Failure(
                    $"RabbitMQ management API response could not be read ({ex.GetType().Name}).",
                    response.StatusCode);
            }
        }
    }

    internal static string QueuePath(RabbitMqSettings settings)
    {
        var path = string.IsNullOrWhiteSpace(settings.VirtualHost)
            ? "api/queues"
            : $"api/queues/{Uri.EscapeDataString(settings.VirtualHost)}";

        return string.Concat(
            path,
            "?page=1&page_size=",
            settings.MaxQueues.ToString(CultureInfo.InvariantCulture),
            "&pagination=true");
    }

    internal static string CreateBasicAuthParameter(string username, string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

    private static RabbitMqTargetResolution ResolveTarget(
        RabbitMqSettings settings,
        string relativePath)
    {
        if (string.IsNullOrWhiteSpace(settings.Username))
        {
            return RabbitMqTargetResolution.Failure("RabbitMQ management API username is not configured.");
        }

        if (!Uri.TryCreate(settings.BaseUrl + "/", UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            return RabbitMqTargetResolution.Failure(
                "RabbitMQ baseUrl must be an absolute http/https URL without embedded credentials.");
        }

        if (!Uri.TryCreate(baseUri, relativePath, out var targetUri))
        {
            return RabbitMqTargetResolution.Failure("RabbitMQ management API URL could not be resolved.");
        }

        return RabbitMqTargetResolution.Success(targetUri);
    }

    private static string ClassifyStatus(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                "RabbitMQ management API authentication failed.",
            HttpStatusCode.Forbidden =>
                "RabbitMQ management API denied access to the diagnostic endpoint.",
            HttpStatusCode.NotFound =>
                "RabbitMQ management API endpoint was not found; confirm the management plugin/base URL.",
            _ =>
                $"RabbitMQ management API returned HTTP {(int)statusCode}."
        };

    private static async Task<string?> ReadBoundedAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream(capacity: Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        var total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private sealed record RabbitMqTargetResolution(
        bool Succeeded,
        Uri? Uri,
        string FailureSummary)
    {
        public static RabbitMqTargetResolution Success(Uri uri) =>
            new(true, uri, string.Empty);

        public static RabbitMqTargetResolution Failure(string summary) =>
            new(false, null, summary);
    }
}
