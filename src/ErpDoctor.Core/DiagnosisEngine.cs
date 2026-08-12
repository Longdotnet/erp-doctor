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

        AddNetworkHttpDiagnoses(results, diagnoses);

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

        var cpuPressure = results.FirstOrDefault(x =>
            x.CheckId == "system.cpu" &&
            x.Status is DiagnosticStatus.Warning or DiagnosticStatus.Critical);

        if (cpuPressure is not null && slowHttp is not null)
        {
            diagnoses.Add(new Diagnosis(
                DiagnosticStatus.Warning,
                "Host CPU pressure may be degrading API response time",
                "System CPU pressure was elevated while an HTTP endpoint responded slowly. This is correlation, not proof; use the process and load evidence to determine whether application, database, or other host workload is consuming CPU.",
                [cpuPressure.Summary, slowHttp.Summary],
                [
                    "Inspect the bounded system.processes snapshot for memory-heavy processes and use OS tooling to confirm CPU-heavy processes if pressure persists.",
                    "On Linux, compare the 1/5/15-minute load averages to distinguish a short CPU spike from sustained CPU or I/O pressure.",
                    "Avoid restarting services solely from one short CPU sample; re-run diagnostics and confirm sustained pressure.",
                    "Re-test the HTTP endpoint after resource pressure subsides."
                ]));
        }

        return diagnoses;
    }

    private static void AddNetworkHttpDiagnoses(
        IReadOnlyList<DiagnosticResult> results,
        ICollection<Diagnosis> diagnoses)
    {
        var tcpFailures = results
            .Where(result =>
                result.CheckId.StartsWith("network.tcp.", StringComparison.OrdinalIgnoreCase) &&
                result.Status == DiagnosticStatus.Critical)
            .ToArray();

        if (tcpFailures.Length == 0)
        {
            return;
        }

        foreach (var http in results.Where(result =>
                     result.CheckId.StartsWith("http.", StringComparison.OrdinalIgnoreCase) &&
                     result.Status == DiagnosticStatus.Critical))
        {
            if (!http.EvidenceOrEmpty.TryGetValue("url", out var url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var matchingTcp = tcpFailures.FirstOrDefault(tcp =>
                MatchesHttpEndpoint(tcp, uri));
            if (matchingTcp is null)
            {
                continue;
            }

            diagnoses.Add(new Diagnosis(
                DiagnosticStatus.Critical,
                "TCP reachability failure likely explains HTTP unavailability",
                "The HTTP endpoint is unavailable and a configured TCP diagnostic for the same host and port also fails. This places the failure below the HTTP application layer and makes DNS, routing, firewall, listener, reverse-proxy, or process availability the immediate investigation path.",
                [matchingTcp.Summary, http.Summary],
                [
                    "Confirm the destination process/reverse proxy is listening on the configured port.",
                    "Inspect firewall, routing, VPN, NAT, security-group, and load-balancer rules between the diagnostic host and destination.",
                    "If DNS also fails or is slow, resolve that before restarting application services.",
                    "Re-run erp-doctor network and the HTTP check after the network/listener issue is corrected."
                ]));
        }
    }

    private static bool MatchesHttpEndpoint(DiagnosticResult tcp, Uri uri)
    {
        if (!tcp.EvidenceOrEmpty.TryGetValue("host", out var host) ||
            !tcp.EvidenceOrEmpty.TryGetValue("port", out var portText) ||
            !int.TryParse(portText, out var port))
        {
            return false;
        }

        return string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase) &&
               port == uri.Port;
    }
}
