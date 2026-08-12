using System.Text.Json;
using ErpDoctor.Core;
using ErpDoctor.Infrastructure.HttpDiagnostics;
using ErpDoctor.Infrastructure.IisDiagnostics;
using ErpDoctor.Infrastructure.NetworkDiagnostics;
using ErpDoctor.Infrastructure.SqlServerDiagnostics;
using ErpDoctor.Infrastructure.SystemDiagnostics;
using ErpDoctor.Infrastructure.WindowsEventDiagnostics;
using ErpDoctor.PluginHost;
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
        var historyPath = GetOption(args, "--history") ?? "erp-doctor-growth.json";
        var leftConfigPath = GetOption(args, "--left");
        var rightConfigPath = GetOption(args, "--right");
        var ignorePrefixes = ParseIgnorePrefixes(GetOption(args, "--ignore"));
        var jsonToStdout = string.Equals(jsonOutput, "-", StringComparison.Ordinal);

        if (jsonToStdout && !SupportsJsonStdout(command))
        {
            Console.Error.WriteLine(
                $"Command '{command}' does not produce the diagnostic-report schema required by --json -. " +
                "Use check, report, system, sql, http, network, iis, eventlog, or plugin.");
            return 2;
        }

        if (jsonToStdout &&
            (!string.IsNullOrWhiteSpace(htmlOutput) || !string.IsNullOrWhiteSpace(bundleOutput)))
        {
            Console.Error.WriteLine("--json - cannot be combined with --html or --bundle; stdout must contain one JSON document only.");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        if (command.Equals("config-diff", StringComparison.OrdinalIgnoreCase))
        {
            return await RunConfigDiffCommandAsync(
                leftConfigPath,
                rightConfigPath,
                ignorePrefixes,
                cts.Token);
        }

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

        if (command.Equals("growth", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await RunGrowthAsync(options, historyPath, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Growth snapshot cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not capture database growth: {ex.Message}");
                return 1;
            }
        }

        var pluginDiscovery = ShouldLoadPlugins(command)
            ? new PluginLoader().Load(options.Plugins, GetConfigDirectory(configPath))
            : new PluginDiscovery(
                Array.Empty<LoadedPlugin>(),
                Array.Empty<PluginLoadIssue>());

        if (command.Equals("plugins", StringComparison.OrdinalIgnoreCase))
        {
            PluginConsoleReport.Write(pluginDiscovery);
            return pluginDiscovery.Issues.Count == 0 ? 0 : 1;
        }

        var category = command.ToLowerInvariant() switch
        {
            "check" => null,
            "report" => null,
            "bundle" => null,
            "system" => "system",
            "sql" => "sql",
            "http" => "http",
            "network" => "network",
            "iis" => "iis",
            "eventlog" => "eventlog",
            "plugin" => "plugin",
            _ => "__unknown__"
        };

        if (category == "__unknown__")
        {
            Console.Error.WriteLine($"Unknown command: {command}");
            PrintHelp();
            return 2;
        }

        var runner = new DiagnosticRunner(BuildChecks(options, pluginDiscovery));
        var context = new DiagnosticContext(options);
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

        if (jsonToStdout)
        {
            Console.Out.WriteLine(DiagnosticJsonReportSerializer.Serialize(report));
        }
        else
        {
            ConsoleReport.Write(report);
        }

        if (!string.IsNullOrWhiteSpace(jsonOutput) && !jsonToStdout)
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

    private static async Task<int> RunConfigDiffCommandAsync(
        string? leftPath,
        string? rightPath,
        IReadOnlyList<string> ignorePrefixes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leftPath) ||
            string.IsNullOrWhiteSpace(rightPath))
        {
            Console.Error.WriteLine("config-diff requires both --left <path> and --right <path>.");
            return 2;
        }

        try
        {
            var leftFullPath = Path.GetFullPath(leftPath);
            var rightFullPath = Path.GetFullPath(rightPath);
            var leftJson = await File.ReadAllTextAsync(leftFullPath, cancellationToken);
            var rightJson = await File.ReadAllTextAsync(rightFullPath, cancellationToken);

            var report = JsonConfigDriftAnalyzer.Compare(
                leftJson,
                rightJson,
                leftFullPath,
                rightFullPath,
                ignorePrefixes);

            ConfigDriftConsoleReport.Write(report);
            return report.Differences.Count == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Configuration comparison cancelled.");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Could not compare configuration: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> RunGrowthAsync(
        ErpDoctorOptions options,
        string historyPath,
        CancellationToken cancellationToken)
    {
        var context = new DiagnosticContext(options);
        var collector = new SqlGrowthSnapshotCollector();
        var store = new SqlGrowthHistoryStore();

        var history = await store.LoadAsync(historyPath, cancellationToken);
        var current = await collector.CaptureAsync(context, cancellationToken);
        var previous = SqlGrowthAnalyzer.FindPrevious(history, current);
        var comparison = previous is null
            ? null
            : SqlGrowthAnalyzer.Compare(previous, current);

        var updatedHistory = store.Append(history, current);
        var savedPath = await store.SaveAsync(
            historyPath,
            updatedHistory,
            cancellationToken);

        GrowthConsoleReport.Write(current, comparison, savedPath);
        return 0;
    }

    private static IReadOnlyList<IDiagnosticCheck> BuildChecks(
        ErpDoctorOptions options,
        PluginDiscovery pluginDiscovery)
    {
        var checks = new List<IDiagnosticCheck>
        {
            new DotNetRuntimeCheck(),
            new MemoryCheck(),
            new CpuUtilizationCheck(),
            new LoadAverageCheck(),
            new TopProcessesCheck(),
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

        foreach (var target in options.Network.Targets)
        {
            checks.Add(new DnsResolutionCheck(target));
            checks.Add(new TcpConnectivityCheck(target));
        }

        foreach (var appPool in options.Iis.AppPools)
        {
            checks.Add(new IisAppPoolCheck(appPool));
        }

        foreach (var site in options.Iis.Sites)
        {
            checks.Add(new IisSiteCheck(site));
        }

        foreach (var eventQuery in options.WindowsEventLog.Queries)
        {
            checks.Add(new WindowsEventLogCheck(eventQuery));
        }

        checks.AddRange(pluginDiscovery.DiagnosticChecks);
        return checks;
    }

    private static async Task<string> WriteJsonReportAsync(
        DiagnosticReport report,
        string path,
        CancellationToken cancellationToken)
    {
        var json = DiagnosticJsonReportSerializer.Serialize(report, writeIndented: true);
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

    private static string GetConfigDirectory(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        return Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
    }

    private static bool ShouldLoadPlugins(string command) =>
        command.Equals("check", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("report", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("bundle", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("plugin", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("plugins", StringComparison.OrdinalIgnoreCase);

    private static bool SupportsJsonStdout(string command) =>
        command.Equals("check", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("report", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("system", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("sql", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("http", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("network", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("iis", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("eventlog", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("plugin", StringComparison.OrdinalIgnoreCase);

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
        value.Equals("--bundle", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--history", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--left", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--right", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--ignore", StringComparison.OrdinalIgnoreCase);

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

    private static IReadOnlyList<string> ParseIgnorePrefixes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value.Split(
            [',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ERP Doctor - read-only diagnostics for boring enterprise applications.

            Usage:
              erp-doctor check [--config erp-doctor.json] [--json report.json| -] [--html report.html] [--bundle support.zip]
              erp-doctor report [--config erp-doctor.json] [--json report.json| -] [--html report.html]
              erp-doctor bundle [--config erp-doctor.json] [--bundle support.zip]
              erp-doctor growth [--config erp-doctor.json] [--history erp-doctor-growth.json]
              erp-doctor config-diff --left appsettings.dev.json --right appsettings.prod.json [--ignore Logging,Serilog]
              erp-doctor system [--config erp-doctor.json] [--json -]
              erp-doctor sql [--config erp-doctor.json] [--json -]
              erp-doctor http [--config erp-doctor.json] [--json -]
              erp-doctor network [--config erp-doctor.json] [--json -]
              erp-doctor iis [--config erp-doctor.json] [--json -]
              erp-doctor eventlog [--config erp-doctor.json] [--json -]
              erp-doctor plugins [--config erp-doctor.json]
              erp-doctor plugin [--config erp-doctor.json] [--json -]

            Commands:
              check        Run every configured diagnostic and correlate likely causes.
              report       Run all checks and write a standalone HTML report by default.
              bundle       Run all checks and write a sanitized ZIP support bundle by default.
              growth       Capture SQL database/table size and compare it with the previous local snapshot.
              config-diff  Compare two local JSON/appsettings files without printing secret values.
              system       Inspect disk, memory, CPU/load pressure, top process working sets, runtime, and OS information.
              sql          Inspect SQL Server connectivity, size, largest tables, blocking, and long requests.
              http         Probe configured HTTP health endpoints.
              network      Resolve configured hosts and test TCP port reachability/latency cross-platform.
              iis          Inspect configured IIS AppPools, sites, bindings, and physical paths on Windows.
              eventlog     Inspect configured recent Windows Event Log errors/warnings.
              plugins      Discover configured plugin assemblies without executing plugin checks.
              plugin       Run only checks contributed by configured plugins.

            Output/state options:
              --json <path>     Write the stable machine-readable report schema as indented JSON.
              --json -          Write one compact diagnostic-report JSON document to stdout and suppress the human report.
                                This mode is intended for CI, scripts, agents, and future MCP/integration wrappers.
              --html <path>     Write a standalone, dependency-free HTML diagnostic report.
              --bundle <path>   Write a sanitized ZIP with report.json, report.html, and manifest.json.
              --history <path>  Local JSON history used by the growth command.

            JSON stdout rules:
              --json - supports check, report, system, sql, http, network, iis, eventlog, and plugin.
              It cannot be combined with --html or --bundle so stdout remains exactly one JSON document.
              Diagnostic/configuration errors are written to stderr; exit codes keep their normal meaning.

            Config drift options:
              --left <path>     Left JSON/appsettings file.
              --right <path>    Right JSON/appsettings file.
              --ignore <paths>  Comma/semicolon-separated path prefixes to ignore.

            Plugin safety:
              Plugin assemblies are executable .NET code and run with ERP Doctor process permissions.
              ERP Doctor loads plugins only from explicit local DLL paths in configuration. Only load
              plugins you trust. Raw plugin exception messages are suppressed by the host.

            Safety:
              Built-in production diagnostics are read-only. Plugin behavior is owned by the plugin
              author and is outside ERP Doctor's built-in read-only guarantee.
            """);
    }
}
