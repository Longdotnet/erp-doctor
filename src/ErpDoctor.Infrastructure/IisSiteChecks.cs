using System.Collections;
using System.Reflection;
using ErpDoctor.Core;

namespace ErpDoctor.Infrastructure.IisDiagnostics;

public sealed record IisSiteSnapshot(
    string State,
    string PhysicalPath,
    IReadOnlyList<string> Bindings);

public sealed class IisSiteCheck(IisSiteOptions site) : IDiagnosticCheck
{
    public string Id => $"iis.site.{Normalize(site.Name)}";
    public string Name => $"IIS Site {site.Name}";
    public string Category => "iis";

    public Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Skipped,
                "IIS site diagnostics require Windows."));
        }

        if (string.IsNullOrWhiteSpace(site.Name))
        {
            return Task.FromResult(new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Skipped,
                "No IIS site name configured."));
        }

        try
        {
            var snapshot = IisSiteInspector.Inspect(site.Name);
            if (snapshot is null)
            {
                return Task.FromResult(new DiagnosticResult(
                    Id,
                    Name,
                    DiagnosticStatus.Critical,
                    $"IIS site '{site.Name}' was not found.",
                    new Dictionary<string, string>
                    {
                        ["site"] = site.Name,
                        ["state"] = "missing"
                    },
                    [
                        "Confirm the configured site name matches IIS.",
                        "Verify the deployment created the expected IIS site before troubleshooting the application itself."
                    ]));
            }

            var physicalPathExists = !site.CheckPhysicalPath ||
                                     (!string.IsNullOrWhiteSpace(snapshot.PhysicalPath) &&
                                      Directory.Exists(snapshot.PhysicalPath));
            return Task.FromResult(IisSiteEvaluator.Evaluate(
                site,
                snapshot,
                physicalPathExists));
        }
        catch (FileNotFoundException ex)
        {
            return Task.FromResult(new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                ex.Message,
                Suggestions:
                [
                    "Confirm IIS Management Scripts and Tools are installed.",
                    "Run this diagnostic on the IIS server that hosts the ERP application."
                ]));
        }
        catch (Exception ex) when (ex is ReflectionTypeLoadException or TargetInvocationException or InvalidOperationException)
        {
            return Task.FromResult(new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                $"Could not inspect IIS site: {GetRootMessage(ex)}",
                Suggestions:
                [
                    "Confirm the process has permission to inspect IIS configuration.",
                    "Confirm IIS management components are installed on this Windows server."
                ]));
        }
    }

    private static string Normalize(string value) =>
        new string(value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray())
            .Trim('-');

    private static string GetRootMessage(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }
}

public static class IisSiteEvaluator
{
    public static DiagnosticResult Evaluate(
        IisSiteOptions site,
        IisSiteSnapshot snapshot,
        bool physicalPathExists)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(snapshot);

        var actualBindings = snapshot.Bindings
            .Select(NormalizeBinding)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedBindings = site.ExpectedBindings
            .Select(NormalizeBinding)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missingBindings = expectedBindings
            .Except(actualBindings, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var started = string.Equals(snapshot.State, "Started", StringComparison.OrdinalIgnoreCase);
        var pathProblem = site.CheckPhysicalPath && !physicalPathExists;

        var status = !started || pathProblem || missingBindings.Length > 0
            ? DiagnosticStatus.Critical
            : DiagnosticStatus.Healthy;

        var problems = new List<string>();
        if (!started)
        {
            problems.Add($"state {snapshot.State}");
        }

        if (pathProblem)
        {
            problems.Add("physical path missing");
        }

        if (missingBindings.Length > 0)
        {
            problems.Add($"{missingBindings.Length} expected binding(s) missing");
        }

        var summary = problems.Count == 0
            ? $"Site Started; {actualBindings.Length} binding(s); physical path OK"
            : string.Join("; ", problems);

        var evidence = new Dictionary<string, string>
        {
            ["site"] = site.Name,
            ["state"] = snapshot.State,
            ["physicalPath"] = snapshot.PhysicalPath,
            ["bindings"] = actualBindings.Length == 0 ? "(none)" : string.Join(" | ", actualBindings)
        };

        if (expectedBindings.Length > 0)
        {
            evidence["expectedBindings"] = string.Join(" | ", expectedBindings);
        }

        if (missingBindings.Length > 0)
        {
            evidence["missingBindings"] = string.Join(" | ", missingBindings);
        }

        var suggestions = new List<string>();
        if (!started)
        {
            suggestions.Add("Inspect why the IIS site is stopped before testing the application endpoint.");
        }

        if (pathProblem)
        {
            suggestions.Add("Confirm the IIS physical path points to an existing release folder.");
        }

        if (missingBindings.Length > 0)
        {
            suggestions.Add("Compare IIS protocol, host, and port bindings with the expected environment configuration.");
        }

        if (suggestions.Count > 0)
        {
            suggestions.Add("ERP Doctor reports IIS state only and does not modify sites or bindings automatically.");
        }

        return new DiagnosticResult(
            $"iis.site.{NormalizeName(site.Name)}",
            $"IIS Site {site.Name}",
            status,
            summary,
            evidence,
            suggestions.Count == 0 ? null : suggestions);
    }

    private static string NormalizeBinding(string value) =>
        value.Trim().ToLowerInvariant();

    private static string NormalizeName(string value) =>
        new string(value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray())
            .Trim('-');
}

internal static class IisSiteInspector
{
    public static IisSiteSnapshot? Inspect(string siteName)
    {
        var assemblyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "inetsrv",
            "Microsoft.Web.Administration.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                "Microsoft.Web.Administration.dll was not found; IIS management components may not be installed.",
                assemblyPath);
        }

        var assembly = Assembly.LoadFrom(assemblyPath);
        var serverManagerType = assembly.GetType(
            "Microsoft.Web.Administration.ServerManager",
            throwOnError: true)
            ?? throw new InvalidOperationException("IIS ServerManager type was not found.");

        using var manager = Activator.CreateInstance(serverManagerType) as IDisposable
            ?? throw new InvalidOperationException("Could not create IIS ServerManager.");
        var sites = GetEnumerable(serverManagerType, manager, "Sites");
        var inspectedSite = sites.Cast<object>().FirstOrDefault(candidate =>
            string.Equals(
                GetString(candidate, "Name"),
                siteName,
                StringComparison.OrdinalIgnoreCase));
        if (inspectedSite is null)
        {
            return null;
        }

        var state = GetValue(inspectedSite, "State")?.ToString() ?? string.Empty;
        var bindings = GetEnumerable(inspectedSite.GetType(), inspectedSite, "Bindings")
            .Cast<object>()
            .Select(binding =>
                $"{GetString(binding, "Protocol")}:{GetString(binding, "BindingInformation")}")
            .ToArray();
        var physicalPath = Environment.ExpandEnvironmentVariables(
            ReadRootPhysicalPath(inspectedSite));

        return new IisSiteSnapshot(state, physicalPath, bindings);
    }

    private static string ReadRootPhysicalPath(object site)
    {
        var applications = GetEnumerable(site.GetType(), site, "Applications");
        var rootApplication = applications.Cast<object>().FirstOrDefault(application =>
            string.Equals(GetString(application, "Path"), "/", StringComparison.Ordinal));
        if (rootApplication is null)
        {
            return string.Empty;
        }

        var virtualDirectories = GetEnumerable(
            rootApplication.GetType(),
            rootApplication,
            "VirtualDirectories");
        var rootVirtualDirectory = virtualDirectories.Cast<object>().FirstOrDefault(directory =>
            string.Equals(GetString(directory, "Path"), "/", StringComparison.Ordinal));

        return rootVirtualDirectory is null
            ? string.Empty
            : GetString(rootVirtualDirectory, "PhysicalPath");
    }

    private static IEnumerable GetEnumerable(Type ownerType, object owner, string propertyName)
    {
        var value = ownerType.GetProperty(propertyName)?.GetValue(owner);
        return value as IEnumerable
            ?? throw new InvalidOperationException($"IIS property '{propertyName}' is unavailable.");
    }

    private static object? GetValue(object owner, string propertyName) =>
        owner.GetType().GetProperty(propertyName)?.GetValue(owner);

    private static string GetString(object owner, string propertyName) =>
        GetValue(owner, propertyName)?.ToString() ?? string.Empty;
}
