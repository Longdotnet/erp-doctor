namespace ErpDoctor.Core;

public enum DiagnosticStatus
{
    Healthy = 0,
    Info = 1,
    Warning = 2,
    Critical = 3,
    Skipped = 4,
    Error = 5
}

public sealed record DiagnosticResult(
    string CheckId,
    string Name,
    DiagnosticStatus Status,
    string Summary,
    IReadOnlyDictionary<string, string>? Evidence = null,
    IReadOnlyList<string>? Suggestions = null,
    TimeSpan? Duration = null)
{
    public IReadOnlyDictionary<string, string> EvidenceOrEmpty =>
        Evidence ?? new Dictionary<string, string>();

    public IReadOnlyList<string> SuggestionsOrEmpty =>
        Suggestions ?? Array.Empty<string>();
}

public interface IDiagnosticCheck
{
    string Id { get; }
    string Name { get; }
    string Category { get; }

    Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken);
}

public sealed record DiagnosticContext(ErpDoctorOptions Options);

public sealed record Diagnosis(
    DiagnosticStatus Status,
    string Title,
    string Explanation,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> SuggestedActions);
