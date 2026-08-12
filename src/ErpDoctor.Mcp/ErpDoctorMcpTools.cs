using System.ComponentModel;
using ErpDoctor.Core;
using ModelContextProtocol.Server;

namespace ErpDoctor.Mcp;

[McpServerToolType]
public static class ErpDoctorMcpTools
{
    [McpServerTool(
        Name = "run_diagnostics",
        Title = "Run ERP Doctor diagnostics",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Runs ERP Doctor's existing read-only diagnostic engine using the server's fixed startup configuration. " +
        "Returns the versioned DiagnosticReport with evidence, statuses, health score, and correlations. " +
        "This tool never repairs, restarts, kills sessions/processes, edits configuration, or accepts a client-selected config path.")]
    public static Task<DiagnosticReport> RunDiagnosticsAsync(
        [Description(
            "Diagnostic scope: check, system, sql, http, network, iis, eventlog, or plugin. " +
            "Use check for the full configured system plus evidence correlations.")]
        string scope,
        McpDiagnosticService diagnostics,
        CancellationToken cancellationToken) =>
        diagnostics.RunAsync(scope, cancellationToken);
}
