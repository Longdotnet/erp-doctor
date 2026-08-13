using System.Text.Json;
using System.Text.Json.Serialization;
using ErpDoctor.Core;

namespace ErpDoctor.Reporting;

public static class DiagnosticReportDiffJsonSerializer
{
    public static string Serialize(
        DiagnosticReportDiff diff,
        bool writeIndented = false)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        return JsonSerializer.Serialize(diff, options);
    }
}
