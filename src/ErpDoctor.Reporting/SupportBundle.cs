using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ErpDoctor.Core;

namespace ErpDoctor.Reporting;

public static class ReportSanitizer
{
    public const string RedactedValue = "[REDACTED]";

    private static readonly Regex InlineSecretPattern = new(
        @"(?i)\b(password|pwd|token|secret|api[-_ ]?key|authorization)\b\s*[:=]\s*(?:Bearer\s+)?[^;,\s&]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerTokenPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DiagnosticReport Sanitize(DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var results = report.Results
            .Select(SanitizeResult)
            .ToArray();
        var diagnoses = report.Diagnoses
            .Select(SanitizeDiagnosis)
            .ToArray();

        return report with
        {
            Results = results,
            Diagnoses = diagnoses
        };
    }

    public static string SanitizeText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var sanitized = InlineSecretPattern.Replace(value, "$1=[REDACTED]");
        return BearerTokenPattern.Replace(sanitized, "Bearer [REDACTED]");
    }

    private static DiagnosticResult SanitizeResult(DiagnosticResult result)
    {
        var evidence = result.EvidenceOrEmpty.ToDictionary(
            pair => pair.Key,
            pair => IsSensitiveKey(pair.Key)
                ? RedactedValue
                : SanitizeText(pair.Value),
            StringComparer.OrdinalIgnoreCase);

        return result with
        {
            Name = SanitizeText(result.Name),
            Summary = SanitizeText(result.Summary),
            Evidence = evidence,
            Suggestions = result.SuggestionsOrEmpty.Select(SanitizeText).ToArray()
        };
    }

    private static Diagnosis SanitizeDiagnosis(Diagnosis diagnosis) =>
        diagnosis with
        {
            Title = SanitizeText(diagnosis.Title),
            Explanation = SanitizeText(diagnosis.Explanation),
            Evidence = diagnosis.Evidence.Select(SanitizeText).ToArray(),
            SuggestedActions = diagnosis.SuggestedActions.Select(SanitizeText).ToArray()
        };

    private static bool IsSensitiveKey(string key)
    {
        var normalized = new string(key
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        return normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Equals("pwd", StringComparison.Ordinal) ||
               normalized.Contains("token", StringComparison.Ordinal) ||
               normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("apikey", StringComparison.Ordinal) ||
               normalized.Contains("authorization", StringComparison.Ordinal) ||
               normalized.Contains("connectionstring", StringComparison.Ordinal);
    }
}

public sealed record SupportBundleManifest(
    string BundleSchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string ReportSchemaVersion,
    string Sanitization,
    IReadOnlyList<string> Entries);

public sealed class SupportBundleBuilder
{
    public const string CurrentBundleSchemaVersion = "1.0";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly HtmlReportRenderer _htmlRenderer;

    public SupportBundleBuilder(HtmlReportRenderer? htmlRenderer = null)
    {
        _htmlRenderer = htmlRenderer ?? new HtmlReportRenderer();
    }

    public async Task<string> WriteAsync(
        DiagnosticReport report,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var sanitizedReport = ReportSanitizer.Sanitize(report);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entries = new[] { "report.json", "report.html", "manifest.json" };
        var manifest = new SupportBundleManifest(
            CurrentBundleSchemaVersion,
            DateTimeOffset.UtcNow,
            sanitizedReport.SchemaVersion,
            "enabled",
            entries);

        var reportJson = JsonSerializer.Serialize(sanitizedReport, JsonOptions);
        var reportHtml = _htmlRenderer.Render(sanitizedReport);
        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);

        await using var file = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous);

        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntryAsync(archive, "report.json", reportJson, cancellationToken);
            await WriteEntryAsync(archive, "report.html", reportHtml, cancellationToken);
            await WriteEntryAsync(archive, "manifest.json", manifestJson, cancellationToken);
        }

        await file.FlushAsync(cancellationToken);
        return fullPath;
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: false);

        await writer.WriteAsync(content.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
