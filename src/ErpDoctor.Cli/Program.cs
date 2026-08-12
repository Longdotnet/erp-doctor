using System.Text.Json;
using System.Text.Json.Serialization;
using ErpDoctor.Core;
using ErpDoctor.Infrastructure.HttpDiagnostics;
using ErpDoctor.Infrastructure.IisDiagnostics;
using ErpDoctor.Infrastructure.SqlServerDiagnostics;
using ErpDoctor.Infrastructure.SystemDiagnostics;
using ErpDoctor.Reporting;

return await ProgramEntry.RunAsync(args);

internal static class ProgramEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(x => x is "-h" or "--help" or "help"))
        {
            PrintHelp();
            return 0;
        }

        var command = GetCommand(args);
        var configPath = GetOption(args, "--config") ?? "erp-doctor.json";
        var jsonOutput = GetOption(args, "--json");
        var htmlOutput = GetOption(args, "--html");
        var bundleOutput = GetOption(args, "--bundle");

        if (command.Equals("report", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(jsonOutput) &&
            string.IsNullOrWhiteSpace(htmlOutput))
        {
            htmlOutput = "erp-doctor-report.html";
        }

        if (command.Equals("bundle", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(bundleOutput))
        {
            bundleOutput = "erp-doctor-support.zip";
        }

        ErpDoctorOptions options;
        try
        {
            options = ErpDoctorOptions.Load(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not load config '{configPath}': {ex.Message}");
            return 2;
        }

        var checks = BuildChecks(options);
        var runner = new DiagnosticRunner(checks);
        var context = new DiagnosticContext(options);
        var category = command.ToLowerInvariant() switch
        {
            "check" => null,
            "report" => null,
            "bundle" => null,
            "system" => "system",
            "sql" => "sql",
            "http" => "http",
            "iis" => "iis",
            _ => "__unknown__"
        };

        if (category == "__unknown__")
        {
            Console.Error.WriteLine($"Unknown command: {command}");
            PrintHelp();
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        IReadOnlyList<DiagnosticResult> results;
        try
        {
            results = await runner.RunAsync(context, category, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Diagnostic run cancelled.");
            return 130;
        }

        var shouldDiagnose = command.Equals("check", StringComparison.OrdinalIgnoreCase) ||
                             command.Equals("report", StringComparison.OrdinalIgnoreCase) ||
                             command.Equals("bundle", StringComparison.OrdinalIgnoreCase);
        var diagnoses = shouldDiagnose
            ? new DiagnosisEngine().Diagnose(results)
            : Array.Empty<Diagnosis>();
        var report = DiagnosticReportFactory.Create(results, diagnoses);

        ConsoleReport.Write(report);

        if (!string.IsNullOrWhiteSpace(jsonOutput))
        {
            var jsonPath = await WriteJsonReportAsync(report, jsonOutput, cts.Token);
            Console.WriteLine();
            Console.WriteLine($"JSON report: {jsonPath}");
        }

        if (!string.IsNullOrWhiteSpace(htmlOutput))
        {
            var html = new HtmlReportRenderer().Render(report);
            var htmlPath = await WriteTextFileAsync(htmlOutput, html, cts.Token);
            Console.WriteLine();
            Console.WriteLine($"HTML report: {htmlPath}");
        }

        if (!string.IsNullOrWhiteSpace(bundleOutput))
        {
            var bundlePath = await new SupportBundleBuilder()
                .WriteAsync(report, bundleOutput, cts.Token);
            Console.WriteLine();
            Console.WriteLine($"Sanitized support bundle: {bundlePath}");
        }

        return results.Any(x => x.Status is DiagnosticStatus.Critical or DiagnosticStatus.Error)
            ? 1
            : 0;
    }

    private static async Task<string> WriteJsonReportAsync(
        DiagnosticReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        var json = JsonSerializer.Serialize(report, options);
        return await WriteTextFileAsync(path, json, cancellationToken);
    }

    private static async Task<string> WriteTextFileAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        return fullPath;
    }

    private static IReadOnlyList<IDiagnosticCheck> BuildChecks(ErpDoctorOptions options)
    {
        var checks = new List<IDiagnosticCheck>
        {
            new DotNetRuntimeCheck(),
            new MemoryCheck(),
            new SqlConnectivityCheck(),
            new SqlDatabaseSizeCheck(),
            new SqlLargestTablesCheck(),
            new SqlBlockingCheck(),
            new SqlLongRunningRequestsCheck()
        };

        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed))
        {
            checks.Add(new DiskSpaceCheck(drive));
        }

        foreach (var endpoint in options.Http.Endpoints)
        {
            checks.Add(new HttpEndpointCheck(endpoint));
        }

        foreach (var appPool in options.Iis.AppPools)
        {
            checks.Add(new IisAppPoolCheck(appPool));
        }

        return checks;
    }

    private static string GetCommand(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (IsOptionWithValue(args[i]))
            {
                i++;
                continue;
            }

            if (!args[i].StartsWith('-'))
            {
                return args[i];
            }
        }

        return "check";
    }

    private static bool IsOptionWithValue(string value) =>
        value.Equals("--config", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--json", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--html", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--bundle", StringComparison.OrdinalIgnoreCase);

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ERP Doctor - read-only diagnostics for boring enterprise applications.

            Usage:
              erp-doctor check [--config erp-doctor.json] [--json report.json] [--html report.html] [--bundle support.zip]
              erp-doctor report [--config erp-doctor.json] [--json report.json] [--html report.html]
              erp-doctor bundle [--config erp-doctor.json] [--bundle support.zip]
              erp-doctor system [--config erp-doctor.json]
              erp-doctor sql [--config erp-doctor.json]
              erp-doctor http [--config erp-doctor.json]
              erp-doctor iis [--config erp-doctor.json]

            Commands:
              check   Run every configured diagnostic and correlate likely causes.
              report  Run all checks and write a standalone HTML report by default.
              bundle  Run all checks and write a sanitized ZIP support bundle by default.
              system  Inspect disk, memory, runtime, and OS information.
              sql     Inspect SQL Server connectivity, size, largest tables, blocking, and long requests.
              http    Probe configured HTTP health endpoints.
              iis     Inspect configured IIS application pools on Windows.

            Report output:
              --json <path>     Write the stable machine-readable report schema as JSON.
              --html <path>     Write a standalone, dependency-free HTML diagnostic report.
              --bundle <path>   Write a sanitized ZIP with report.json, report.html, and manifest.json.

            Safety:
              ERP Doctor v0.3 is read-only. Support bundles redact secret-like evidence before
              serialization and do not include the source configuration file.
            """);
    }
}

internal static class ConsoleReport
{
    public static void Write(DiagnosticReport report)
    {
        var results = report.Results;
        var diagnoses = report.Diagnoses;

        Console.WriteLine();
        Console.WriteLine("ERP Doctor");
        Console.WriteLine(new string('─', 64));
        Console.WriteLine($"Health score: {report.HealthScore}/100 | Overall: {report.OverallStatus}");

        foreach (var group in results.GroupBy(GetCategory))
        {
            Console.WriteLine();
            Console.WriteLine(group.Key.ToUpperInvariant());
            Console.WriteLine(new string('─', 64));

            foreach (var result in group)
            {
                var oldColor = Console.ForegroundColor;
                Console.ForegroundColor = GetColor(result.Status);
                Console.Write(GetSymbol(result.Status));
                Console.ForegroundColor = oldColor;

                Console.WriteLine($" {result.Name,-30} {result.Summary}");

                foreach (var suggestion in result.SuggestionsOrEmpty)
                {
                    Console.WriteLine($"    -> {suggestion}");
                }
            }
        }

        if (diagnoses.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("DIAGNOSIS");
            Console.WriteLine(new string('─', 64));

            foreach (var diagnosis in diagnoses)
            {
                var oldColor = Console.ForegroundColor;
                Console.ForegroundColor = GetColor(diagnosis.Status);
                Console.WriteLine($"{diagnosis.Status.ToString().ToUpperInvariant()}: {diagnosis.Title}");
                Console.ForegroundColor = oldColor;

                Console.WriteLine(diagnosis.Explanation);
                foreach (var evidence in diagnosis.Evidence)
                {
                    Console.WriteLine($"  evidence: {evidence}");
                }

                foreach (var action in diagnosis.SuggestedActions)
                {
                    Console.WriteLine($"  -> {action}");
                }

                Console.WriteLine();
            }
        }

        var summary = report.Summary;
        Console.WriteLine();
        Console.WriteLine(
            $"Summary: {summary.Healthy} healthy | {summary.Info} info | {summary.Warning} warning | " +
            $"{summary.Critical} critical | {summary.Error} error | {summary.Skipped} skipped");
    }

    private static string GetCategory(DiagnosticResult result)
    {
        var index = result.CheckId.IndexOf('.');
        return index < 0 ? "general" : result.CheckId[..index];
    }

    private static string GetSymbol(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => "✓",
        DiagnosticStatus.Info => "i",
        DiagnosticStatus.Warning => "!",
        DiagnosticStatus.Critical => "✗",
        DiagnosticStatus.Skipped => "-",
        DiagnosticStatus.Error => "x",
        _ => "?"
    };

    private static ConsoleColor GetColor(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => ConsoleColor.Green,
        DiagnosticStatus.Info => ConsoleColor.Cyan,
        DiagnosticStatus.Warning => ConsoleColor.Yellow,
        DiagnosticStatus.Critical => ConsoleColor.Red,
        DiagnosticStatus.Skipped => ConsoleColor.DarkGray,
        DiagnosticStatus.Error => ConsoleColor.Magenta,
        _ => Console.ForegroundColor
    };
}
