using System.Text.Json;
using System.Text.Json.Serialization;
using ErpDoctor.Core;

namespace ErpDoctor.Reporting;

public static class DiagnosticJsonReportSerializer
{
    public static string Serialize(DiagnosticReport report, bool writeIndented = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, CreateOptions(writeIndented));
    }

    public static DiagnosticReport Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException("Diagnostic report JSON is empty.");
        }

        return JsonSerializer.Deserialize<DiagnosticReport>(json, CreateOptions(writeIndented: false))
            ?? throw new JsonException("Diagnostic report JSON did not contain a report object.");
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
