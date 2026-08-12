using System.Diagnostics;

namespace ErpDoctor.Core;

public sealed class DiagnosticRunner(IEnumerable<IDiagnosticCheck> checks)
{
    private readonly IReadOnlyList<IDiagnosticCheck> _checks = checks.ToArray();

    public async Task<IReadOnlyList<DiagnosticResult>> RunAsync(
        DiagnosticContext context,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        var selected = string.IsNullOrWhiteSpace(category)
            ? _checks
            : _checks.Where(x =>
                string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        var results = new List<DiagnosticResult>(selected.Count);

        foreach (var check in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await check.ExecuteAsync(context, cancellationToken);
                results.Add(result with { Duration = stopwatch.Elapsed });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new DiagnosticResult(
                    check.Id,
                    check.Name,
                    DiagnosticStatus.Error,
                    ex.Message,
                    Suggestions:
                    [
                        "Inspect the check-specific permissions and configuration.",
                        "Run again with a narrower command to isolate the failing diagnostic."
                    ],
                    Duration: stopwatch.Elapsed));
            }
        }

        return results;
    }
}
