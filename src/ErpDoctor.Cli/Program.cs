using System.Text.Json;
using ErpDoctor.Core;
using ErpDoctor.Infrastructure.HttpDiagnostics;
using ErpDoctor.Infrastructure.IisDiagnostics;
using ErpDoctor.Infrastructure.SqlServerDiagnostics;
using ErpDoctor.Infrastructure.SystemDiagnostics;

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

        var command = args.FirstOrDefault(x => !x.StartsWith('-')) ?? "check";
        var configPath = GetOption(args, "--config") ?? "erp-doctor.json";
        var jsonOutput = GetOption(args, "--json");

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

        var diagnoses = command.Equals("check", StringComparison.OrdinalIgnoreCase)
            ? new DiagnosisEngine().Diagnose(results)
            : Array.Empty<Diagnosis>();

        ConsoleReport.Write(results, diagnoses);

        if (!string.IsNullOrWhiteSpace(jsonOutput))
        {
            var payload = new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                results,
                diagnoses
            };

            var json = JsonSerializer.Serialize(
                payload,
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(jsonOutput, json);
            Console.WriteLine();
            Console.WriteLine($"JSON report: {Path.GetFullPath(jsonOutput)}");
        }

        return results.Any(x => x.Status is DiagnosticStatus.Critical or DiagnosticStatus.Error)
            ? 1
            : 0;
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
              erp-doctor check [--config erp-doctor.json] [--json report.json]
              erp-doctor system [--config erp-doctor.json]
              erp-doctor sql [--config erp-doctor.json]
              erp-doctor http [--config erp-doctor.json]
              erp-doctor iis [--config erp-doctor.json]

            Commands:
              check   Run every configured diagnostic and correlate likely causes.
              system  Inspect disk, memory, runtime, and OS information.
              sql     Inspect SQL Server connectivity, size, largest tables, blocking, and long requests.
              http    Probe configured HTTP health endpoints.
              iis     Inspect configured IIS application pools on Windows.

            Safety:
              ERP Doctor v0.1 is read-only. It does not restart IIS, kill SQL sessions,
              shrink databases, delete logs, or modify ERP data.
            """);
    }
}

internal static class ConsoleReport
{
    public static void Write(
        IReadOnlyList<DiagnosticResult> results,
        IReadOnlyList<Diagnosis> diagnoses)
    {
        Console.WriteLine();
        Console.WriteLine("ERP Doctor");
        Console.WriteLine(new string('─', 64));

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

        var healthy = results.Count(x => x.Status == DiagnosticStatus.Healthy);
        var warning = results.Count(x => x.Status == DiagnosticStatus.Warning);
        var critical = results.Count(x => x.Status == DiagnosticStatus.Critical);
        var error = results.Count(x => x.Status == DiagnosticStatus.Error);

        Console.WriteLine();
        Console.WriteLine($"Summary: {healthy} healthy | {warning} warning | {critical} critical | {error} error");
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
