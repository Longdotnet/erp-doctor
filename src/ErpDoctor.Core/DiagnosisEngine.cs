namespace ErpDoctor.Core;

public sealed class DiagnosisEngine
{
    public IReadOnlyList<Diagnosis> Diagnose(IReadOnlyList<DiagnosticResult> results)
    {
        var diagnoses = new List<Diagnosis>();

        var diskCritical = results.FirstOrDefault(x =>
            x.CheckId.StartsWith("system.disk.", StringComparison.OrdinalIgnoreCase) &&
            x.Status == DiagnosticStatus.Critical);

        var iisStopped = results.FirstOrDefault(x =>
            x.CheckId.StartsWith("iis.apppool.", StringComparison.OrdinalIgnoreCase) &&
            x.Status == DiagnosticStatus.Critical);

        var httpUnavailable = results.FirstOrDefault(x =>
            x.CheckId.StartsWith("http.", StringComparison.OrdinalIgnoreCase) &&
            x.Status == DiagnosticStatus.Critical);

        if (diskCritical is not null && iisStopped is not null && httpUnavailable is not null)
        {
            diagnoses.Add(new Diagnosis(
                DiagnosticStatus.Critical,
                "Application unavailable with critically low disk space",
                "The API is unavailable while an IIS application pool is stopped and the server has critically low disk space. Low disk space can prevent application logging, temporary file creation, deployments, and process startup.",
                [
                    diskCritical.Summary,
                    iisStopped.Summary,
                    httpUnavailable.Summary
                ],
                [
                    "Free disk space before attempting repeated restarts.",
                    "Inspect Windows Event Viewer and the application log for the AppPool stop reason.",
                    "Start the AppPool only after the underlying resource issue is understood.",
                    "Re-run erp-doctor check and confirm the HTTP endpoint becomes healthy."
                ]));
        }
        else if (iisStopped is not null && httpUnavailable is not null)
        {
            diagnoses.Add(new Diagnosis(
                DiagnosticStatus.Critical,
                "IIS application pool is a likely cause of API unavailability",
                "The configured IIS application pool is stopped and the HTTP health endpoint is unavailable.",
                [iisStopped.Summary, httpUnavailable.Summary],
                [
                    "Inspect the AppPool identity and Windows Event Viewer.",
                    "Check application startup logs and configuration.",
                    "Start the AppPool after resolving the underlying error, then re-run the health check."
                ]));
        }

        var blocking = results.FirstOrDefault(x =>
            x.CheckId == "sql.blocking" &&
            x.Status is DiagnosticStatus.Warning or DiagnosticStatus.Critical);

        var slowHttp = results.FirstOrDefault(x =>
            x.CheckId.StartsWith("http.", StringComparison.OrdinalIgnoreCase) &&
            x.EvidenceOrEmpty.TryGetValue("latencyMs", out var latency) &&
            long.TryParse(latency, out var latencyMs) &&
            latencyMs >= 1500);

        if (blocking is not null && slowHttp is not null)
        {
            diagnoses.Add(new Diagnosis(
                DiagnosticStatus.Warning,
                "SQL Server blocking may be degrading API response time",
                "Blocking sessions were detected while an HTTP endpoint was responding slowly. This is correlation, not proof; inspect the blocking chain before taking action.",
                [blocking.Summary, slowHttp.Summary],
                [
                    "Inspect the blocking and blocked session IDs.",
                    "Identify the transaction holding locks and its owning application.",
                    "Avoid killing sessions automatically; confirm business impact first.",
                    "Re-run the SQL and HTTP diagnostics after the transaction clears."
                ]));
        }

        return diagnoses;
    }
}
