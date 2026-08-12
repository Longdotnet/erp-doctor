using System.IO.Compression;
using ErpDoctor.Core;
using ErpDoctor.Reporting;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class SupportBundleTests
{
    [Fact]
    public void Sanitize_RedactsSensitiveEvidenceAndInlineValues()
    {
        var report = CreateSensitiveReport();

        var sanitized = ReportSanitizer.Sanitize(report);
        var result = Assert.Single(sanitized.Results);

        Assert.Equal(ReportSanitizer.RedactedValue, result.EvidenceOrEmpty["connectionString"]);
        Assert.False(result.Summary.Contains("remove-me", StringComparison.Ordinal));
        Assert.False(result.EvidenceOrEmpty["safe"].Contains("remove-me-too", StringComparison.Ordinal));
        Assert.False(result.SuggestionsOrEmpty[0].Contains("remove-inline", StringComparison.Ordinal));
        Assert.False(Assert.Single(sanitized.Diagnoses).Evidence[0]
            .Contains("remove-diagnosis", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteAsync_CreatesSanitizedStandaloneBundle()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"erp-doctor-support-{Guid.NewGuid():N}.zip");

        try
        {
            var output = await new SupportBundleBuilder().WriteAsync(
                CreateSensitiveReport(),
                path,
                CancellationToken.None);

            Assert.Equal(Path.GetFullPath(path), output);
            Assert.True(File.Exists(output));

            using var archive = ZipFile.OpenRead(output);
            Assert.NotNull(archive.GetEntry("report.json"));
            Assert.NotNull(archive.GetEntry("report.html"));
            Assert.NotNull(archive.GetEntry("manifest.json"));
            Assert.Equal(3, archive.Entries.Count);

            var json = await ReadEntryAsync(archive, "report.json");
            var html = await ReadEntryAsync(archive, "report.html");
            var manifest = await ReadEntryAsync(archive, "manifest.json");

            Assert.False(json.Contains("remove-me", StringComparison.Ordinal));
            Assert.False(json.Contains("remove-me-too", StringComparison.Ordinal));
            Assert.True(json.Contains(ReportSanitizer.RedactedValue, StringComparison.Ordinal));
            Assert.False(html.Contains("remove-me", StringComparison.Ordinal));
            Assert.True(manifest.Contains("report.json", StringComparison.Ordinal));
            Assert.True(manifest.Contains("report.html", StringComparison.Ordinal));
            Assert.True(manifest.Contains("manifest.json", StringComparison.Ordinal));
            Assert.True(manifest.Contains("enabled", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static DiagnosticReport CreateSensitiveReport()
    {
        var results = new[]
        {
            new DiagnosticResult(
                "http.erp-api",
                "ERP API",
                DiagnosticStatus.Warning,
                "Request failed with token=remove-me",
                new Dictionary<string, string>
                {
                    ["connectionString"] = "remove-entire-value",
                    ["safe"] = "password=remove-me-too; timeout=30"
                },
                ["Rotate apiKey=remove-inline and retry."])
        };

        var diagnoses = new[]
        {
            new Diagnosis(
                DiagnosticStatus.Warning,
                "Dependency authentication failed",
                "A downstream request included token=remove-explanation",
                ["token=remove-diagnosis"],
                ["Verify secret=remove-action outside the bundle."])
        };

        return DiagnosticReportFactory.Create(
            results,
            diagnoses,
            new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name)
            ?? throw new InvalidOperationException($"Missing bundle entry: {name}");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
