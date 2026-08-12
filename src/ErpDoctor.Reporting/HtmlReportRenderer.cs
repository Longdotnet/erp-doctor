using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using ErpDoctor.Core;

namespace ErpDoctor.Reporting;

public sealed class HtmlReportRenderer
{
    private readonly HtmlEncoder _encoder;

    public HtmlReportRenderer(HtmlEncoder? encoder = null)
    {
        _encoder = encoder ?? HtmlEncoder.Default;
    }

    public string Render(DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var html = new StringBuilder(24_000);
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.AppendLine("<title>ERP Doctor diagnostic report</title>");
        html.AppendLine("<style>");
        html.AppendLine(Css);
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<main class=\"page\">");

        AppendHeader(html, report);
        AppendSummary(html, report);
        AppendDiagnoses(html, report.Diagnoses);
        AppendDiagnostics(html, report.Results);
        AppendFooter(html, report);

        html.AppendLine("</main>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    private void AppendHeader(StringBuilder html, DiagnosticReport report)
    {
        html.AppendLine("<header class=\"hero\">");
        html.AppendLine("<div>");
        html.AppendLine("<p class=\"eyebrow\">READ-ONLY ENTERPRISE DIAGNOSTICS</p>");
        html.AppendLine("<h1>ERP Doctor</h1>");
        html.Append("<p class=\"muted\">Generated ")
            .Append(E(FormatDate(report.GeneratedAtUtc)))
            .AppendLine(" UTC</p>");
        html.AppendLine("</div>");
        html.Append("<div class=\"score score-")
            .Append(StatusClass(report.OverallStatus))
            .AppendLine("\">");
        html.Append("<strong>").Append(report.HealthScore).AppendLine("</strong>");
        html.AppendLine("<span>health score</span>");
        html.Append("<em>").Append(E(report.OverallStatus.ToString())).AppendLine("</em>");
        html.AppendLine("</div>");
        html.AppendLine("</header>");
    }

    private static void AppendSummary(StringBuilder html, DiagnosticReport report)
    {
        var summary = report.Summary;
        html.AppendLine("<section>");
        html.AppendLine("<h2>At a glance</h2>");
        html.AppendLine("<div class=\"metrics\">");
        AppendMetric(html, "Healthy", summary.Healthy, "healthy");
        AppendMetric(html, "Info", summary.Info, "info");
        AppendMetric(html, "Warning", summary.Warning, "warning");
        AppendMetric(html, "Critical", summary.Critical, "critical");
        AppendMetric(html, "Error", summary.Error, "error");
        AppendMetric(html, "Skipped", summary.Skipped, "skipped");
        html.AppendLine("</div>");
        html.AppendLine("</section>");
    }

    private static void AppendMetric(
        StringBuilder html,
        string label,
        int value,
        string statusClass)
    {
        html.Append("<div class=\"metric metric-")
            .Append(statusClass)
            .Append("\"><strong>")
            .Append(value)
            .Append("</strong><span>")
            .Append(label)
            .AppendLine("</span></div>");
    }

    private void AppendDiagnoses(StringBuilder html, IReadOnlyList<Diagnosis> diagnoses)
    {
        html.AppendLine("<section>");
        html.AppendLine("<div class=\"section-heading\"><h2>Diagnosis</h2><span>correlated findings</span></div>");

        if (diagnoses.Count == 0)
        {
            html.AppendLine("<div class=\"empty\">No cross-check diagnosis was produced for this run.</div>");
            html.AppendLine("</section>");
            return;
        }

        foreach (var diagnosis in diagnoses)
        {
            html.Append("<article class=\"diagnosis status-")
                .Append(StatusClass(diagnosis.Status))
                .AppendLine("\">");
            html.Append("<div class=\"badge\">")
                .Append(E(diagnosis.Status.ToString()))
                .AppendLine("</div>");
            html.Append("<h3>").Append(E(diagnosis.Title)).AppendLine("</h3>");
            html.Append("<p>").Append(E(diagnosis.Explanation)).AppendLine("</p>");

            AppendStringList(html, "Evidence", diagnosis.Evidence);
            AppendStringList(html, "Suggested actions", diagnosis.SuggestedActions);
            html.AppendLine("</article>");
        }

        html.AppendLine("</section>");
    }

    private void AppendDiagnostics(StringBuilder html, IReadOnlyList<DiagnosticResult> results)
    {
        html.AppendLine("<section>");
        html.AppendLine("<div class=\"section-heading\"><h2>Diagnostics</h2><span>evidence by subsystem</span></div>");

        foreach (var group in results.GroupBy(GetCategory, StringComparer.OrdinalIgnoreCase))
        {
            html.Append("<div class=\"category\"><h3>")
                .Append(E(group.Key.ToUpperInvariant()))
                .AppendLine("</h3>");

            foreach (var result in group)
            {
                AppendDiagnostic(html, result);
            }

            html.AppendLine("</div>");
        }

        html.AppendLine("</section>");
    }

    private void AppendDiagnostic(StringBuilder html, DiagnosticResult result)
    {
        html.Append("<article class=\"check status-")
            .Append(StatusClass(result.Status))
            .AppendLine("\">");
        html.AppendLine("<div class=\"check-main\">");
        html.Append("<span class=\"status-dot\" aria-hidden=\"true\"></span>");
        html.AppendLine("<div>");
        html.Append("<h4>").Append(E(result.Name)).AppendLine("</h4>");
        html.Append("<p>").Append(E(result.Summary)).AppendLine("</p>");
        html.AppendLine("</div>");
        html.Append("<div class=\"check-meta\"><span>")
            .Append(E(result.Status.ToString()))
            .Append("</span>");

        if (result.Duration is { } duration)
        {
            html.Append("<small>")
                .Append(duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture))
                .Append(" ms</small>");
        }

        html.AppendLine("</div></div>");

        if (result.EvidenceOrEmpty.Count > 0)
        {
            html.AppendLine("<details><summary>Evidence</summary><dl>");
            foreach (var evidence in result.EvidenceOrEmpty)
            {
                html.Append("<dt>").Append(E(evidence.Key)).Append("</dt><dd>")
                    .Append(E(evidence.Value)).AppendLine("</dd>");
            }
            html.AppendLine("</dl></details>");
        }

        if (result.SuggestionsOrEmpty.Count > 0)
        {
            AppendStringList(html, "Suggestions", result.SuggestionsOrEmpty);
        }

        html.AppendLine("</article>");
    }

    private void AppendStringList(
        StringBuilder html,
        string title,
        IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        html.Append("<div class=\"list-block\"><strong>")
            .Append(E(title))
            .AppendLine("</strong><ul>");

        foreach (var value in values)
        {
            html.Append("<li>").Append(E(value)).AppendLine("</li>");
        }

        html.AppendLine("</ul></div>");
    }

    private static void AppendFooter(StringBuilder html, DiagnosticReport report)
    {
        html.AppendLine("<footer>");
        html.Append("ERP Doctor report schema ")
            .Append(report.SchemaVersion)
            .AppendLine(". Diagnostics are read-only; recommendations require operator judgment.");
        html.AppendLine("</footer>");
    }

    private string E(string value) => _encoder.Encode(value);

    private static string GetCategory(DiagnosticResult result)
    {
        var index = result.CheckId.IndexOf('.');
        return index < 0 ? "general" : result.CheckId[..index];
    }

    private static string StatusClass(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => "healthy",
        DiagnosticStatus.Info => "info",
        DiagnosticStatus.Warning => "warning",
        DiagnosticStatus.Critical => "critical",
        DiagnosticStatus.Skipped => "skipped",
        DiagnosticStatus.Error => "error",
        _ => "info"
    };

    private static string FormatDate(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private const string Css = """
        :root {
          color-scheme: light;
          --bg: #f6f7f9;
          --card: #ffffff;
          --ink: #111827;
          --muted: #6b7280;
          --line: #e5e7eb;
          --healthy: #15803d;
          --info: #0369a1;
          --warning: #b45309;
          --critical: #b91c1c;
          --error: #7e22ce;
          --skipped: #6b7280;
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--ink); font: 14px/1.55 ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
        .page { width: min(1120px, calc(100% - 32px)); margin: 32px auto 64px; }
        .hero { display: flex; justify-content: space-between; gap: 24px; align-items: center; padding: 28px 30px; background: #111827; color: white; border-radius: 18px; box-shadow: 0 12px 36px rgba(17,24,39,.16); }
        h1, h2, h3, h4, p { margin-top: 0; }
        h1 { margin-bottom: 4px; font-size: 34px; letter-spacing: -.03em; }
        h2 { font-size: 20px; }
        h3 { font-size: 16px; }
        h4 { margin-bottom: 4px; font-size: 15px; }
        .eyebrow { margin-bottom: 8px; color: #93c5fd; font-size: 11px; font-weight: 800; letter-spacing: .14em; }
        .muted { margin-bottom: 0; color: #cbd5e1; }
        .score { min-width: 130px; text-align: center; padding: 14px 18px; border: 1px solid rgba(255,255,255,.16); border-radius: 14px; background: rgba(255,255,255,.06); }
        .score strong { display: block; font-size: 36px; line-height: 1; }
        .score span, .score em { display: block; }
        .score span { margin: 4px 0; color: #cbd5e1; font-size: 11px; text-transform: uppercase; letter-spacing: .08em; }
        .score em { font-style: normal; font-weight: 800; }
        .score-healthy em { color: #86efac; } .score-info em { color: #7dd3fc; } .score-warning em { color: #fdba74; } .score-critical em { color: #fca5a5; } .score-error em { color: #d8b4fe; }
        section { margin-top: 30px; }
        .section-heading { display: flex; align-items: baseline; justify-content: space-between; gap: 16px; }
        .section-heading span { color: var(--muted); font-size: 12px; }
        .metrics { display: grid; grid-template-columns: repeat(6, 1fr); gap: 10px; }
        .metric { background: var(--card); border: 1px solid var(--line); border-top-width: 3px; border-radius: 12px; padding: 14px; }
        .metric strong { display: block; font-size: 24px; }
        .metric span { color: var(--muted); font-size: 12px; }
        .metric-healthy { border-top-color: var(--healthy); } .metric-info { border-top-color: var(--info); } .metric-warning { border-top-color: var(--warning); } .metric-critical { border-top-color: var(--critical); } .metric-error { border-top-color: var(--error); } .metric-skipped { border-top-color: var(--skipped); }
        .diagnosis, .check, .empty { background: var(--card); border: 1px solid var(--line); border-radius: 12px; }
        .diagnosis { padding: 18px 20px; margin-bottom: 12px; border-left-width: 4px; }
        .check { padding: 14px 16px; margin-bottom: 8px; }
        .empty { padding: 18px; color: var(--muted); }
        .status-healthy { border-left-color: var(--healthy); } .status-info { border-left-color: var(--info); } .status-warning { border-left-color: var(--warning); } .status-critical { border-left-color: var(--critical); } .status-error { border-left-color: var(--error); } .status-skipped { border-left-color: var(--skipped); }
        .badge { display: inline-block; margin-bottom: 8px; padding: 3px 8px; border-radius: 999px; background: #f3f4f6; color: #374151; font-size: 10px; font-weight: 800; letter-spacing: .07em; text-transform: uppercase; }
        .category { margin-bottom: 20px; }
        .category > h3 { color: var(--muted); font-size: 12px; letter-spacing: .08em; }
        .check-main { display: flex; align-items: flex-start; gap: 10px; }
        .check-main > div:nth-child(2) { min-width: 0; flex: 1; }
        .check-main p { margin-bottom: 0; color: #374151; }
        .status-dot { width: 9px; height: 9px; flex: 0 0 9px; margin-top: 6px; border-radius: 999px; background: var(--skipped); }
        .status-healthy .status-dot { background: var(--healthy); } .status-info .status-dot { background: var(--info); } .status-warning .status-dot { background: var(--warning); } .status-critical .status-dot { background: var(--critical); } .status-error .status-dot { background: var(--error); }
        .check-meta { display: flex; flex-direction: column; align-items: flex-end; min-width: 80px; color: var(--muted); font-size: 11px; text-transform: uppercase; }
        .check-meta small { margin-top: 2px; text-transform: none; }
        details { margin: 12px 0 0 19px; padding-top: 10px; border-top: 1px solid var(--line); }
        summary { cursor: pointer; color: #374151; font-weight: 700; }
        dl { display: grid; grid-template-columns: minmax(120px, 220px) 1fr; gap: 6px 14px; margin-bottom: 0; }
        dt { color: var(--muted); } dd { margin: 0; overflow-wrap: anywhere; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 12px; }
        .list-block { margin-top: 12px; }
        .list-block ul { margin: 6px 0 0; padding-left: 21px; }
        footer { margin-top: 32px; padding-top: 18px; border-top: 1px solid var(--line); color: var(--muted); font-size: 12px; }
        @media (max-width: 820px) { .metrics { grid-template-columns: repeat(3, 1fr); } }
        @media (max-width: 560px) { .page { width: min(100% - 20px, 1120px); margin-top: 10px; } .hero { align-items: flex-start; flex-direction: column; border-radius: 12px; } .score { width: 100%; } .metrics { grid-template-columns: repeat(2, 1fr); } .check-main { flex-wrap: wrap; } .check-meta { width: 100%; align-items: flex-start; margin-left: 19px; } dl { grid-template-columns: 1fr; } }
        @media print { body { background: white; } .page { width: 100%; margin: 0; } .hero { box-shadow: none; } details { break-inside: avoid; } }
        """;
}
