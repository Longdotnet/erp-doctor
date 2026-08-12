using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using ErpDoctor.Core;
using ErpDoctor.PluginSdk;

namespace ErpDoctor.PluginHost;

public sealed record LoadedPlugin(
    string Id,
    string Name,
    string Version,
    string AssemblyPath,
    IReadOnlyList<IDiagnosticCheck> Checks);

public sealed record PluginLoadIssue(
    string AssemblyPath,
    string Summary);

public sealed record PluginDiscovery(
    IReadOnlyList<LoadedPlugin> Plugins,
    IReadOnlyList<PluginLoadIssue> Issues)
{
    public IReadOnlyList<IDiagnosticCheck> DiagnosticChecks =>
        Plugins.SelectMany(plugin => plugin.Checks)
            .Concat(Issues.Select((issue, index) =>
                (IDiagnosticCheck)new PluginLoadIssueCheck(index + 1, issue)))
            .ToArray();
}

public sealed class PluginLoader
{
    public PluginDiscovery Load(PluginOptions options, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);

        var root = Path.GetFullPath(
            string.IsNullOrWhiteSpace(baseDirectory)
                ? Environment.CurrentDirectory
                : baseDirectory);
        var plugins = new List<LoadedPlugin>();
        var issues = new List<PluginLoadIssue>();
        var pluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var configuredPath in options.Assemblies)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                issues.Add(new PluginLoadIssue("[empty]", "Plugin assembly path is empty."));
                continue;
            }

            if (configuredPath.Contains("://", StringComparison.Ordinal))
            {
                issues.Add(new PluginLoadIssue(
                    configuredPath,
                    "Plugin assembly must be an explicit local filesystem path; URLs are not supported."));
                continue;
            }

            var assemblyPath = ResolvePath(root, configuredPath);
            if (!string.Equals(Path.GetExtension(assemblyPath), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new PluginLoadIssue(
                    assemblyPath,
                    "Plugin assembly path must point to a .dll file."));
                continue;
            }

            if (!File.Exists(assemblyPath))
            {
                issues.Add(new PluginLoadIssue(
                    assemblyPath,
                    "Plugin assembly file does not exist."));
                continue;
            }

            Assembly assembly;
            try
            {
                assembly = LoadAssembly(assemblyPath);
            }
            catch (Exception ex) when (ex is FileLoadException or FileNotFoundException or BadImageFormatException)
            {
                issues.Add(new PluginLoadIssue(
                    assemblyPath,
                    $"Could not load plugin assembly ({ex.GetType().Name})."));
                continue;
            }

            IReadOnlyList<Type> pluginTypes;
            try
            {
                pluginTypes = assembly.GetExportedTypes()
                    .Where(type =>
                        !type.IsAbstract &&
                        !type.IsInterface &&
                        typeof(IErpDoctorPlugin).IsAssignableFrom(type))
                    .ToArray();
            }
            catch (ReflectionTypeLoadException)
            {
                issues.Add(new PluginLoadIssue(
                    assemblyPath,
                    "Could not inspect plugin types because one or more assembly dependencies failed to load."));
                continue;
            }

            if (pluginTypes.Count == 0)
            {
                issues.Add(new PluginLoadIssue(
                    assemblyPath,
                    "No public IErpDoctorPlugin implementation was found."));
                continue;
            }

            foreach (var pluginType in pluginTypes)
            {
                LoadPluginType(
                    pluginType,
                    assemblyPath,
                    root,
                    options,
                    pluginIds,
                    plugins,
                    issues);
            }
        }

        return new PluginDiscovery(plugins, issues);
    }

    private static void LoadPluginType(
        Type pluginType,
        string assemblyPath,
        string root,
        PluginOptions options,
        ISet<string> pluginIds,
        ICollection<LoadedPlugin> plugins,
        ICollection<PluginLoadIssue> issues)
    {
        if (pluginType.GetConstructor(Type.EmptyTypes) is null)
        {
            issues.Add(new PluginLoadIssue(
                assemblyPath,
                $"Plugin type '{pluginType.FullName}' requires a public parameterless constructor."));
            return;
        }

        IErpDoctorPlugin plugin;
        try
        {
            plugin = (IErpDoctorPlugin)Activator.CreateInstance(pluginType)!;
        }
        catch (Exception ex)
        {
            issues.Add(new PluginLoadIssue(
                assemblyPath,
                $"Plugin type '{pluginType.FullName}' could not be constructed ({ex.GetType().Name})."));
            return;
        }

        if (!IsValidId(plugin.Id))
        {
            issues.Add(new PluginLoadIssue(
                assemblyPath,
                $"Plugin type '{pluginType.FullName}' returned an invalid plugin ID. Use 1-64 letters, digits, '.', '_' or '-'."));
            return;
        }

        if (!pluginIds.Add(plugin.Id))
        {
            issues.Add(new PluginLoadIssue(
                assemblyPath,
                $"Duplicate plugin ID '{plugin.Id}' was ignored."));
            return;
        }

        if (plugin.ApiVersion != PluginApi.CurrentVersion)
        {
            issues.Add(new PluginLoadIssue(
                assemblyPath,
                $"Plugin '{plugin.Id}' targets API v{plugin.ApiVersion}; this ERP Doctor host requires API v{PluginApi.CurrentVersion}."));
            return;
        }

        var configuration = TryGetSetting(options.Settings, plugin.Id, out var setting)
            ? setting
            : (JsonElement?)null;
        var pluginContext = new PluginContext(configuration, root);

        IReadOnlyList<IPluginCheck> pluginChecks;
        try
        {
            pluginChecks = plugin.CreateChecks(pluginContext) ?? Array.Empty<IPluginCheck>();
        }
        catch (Exception ex)
        {
            issues.Add(new PluginLoadIssue(
                assemblyPath,
                $"Plugin '{plugin.Id}' failed while creating checks ({ex.GetType().Name})."));
            return;
        }

        var adaptedChecks = new List<IDiagnosticCheck>();
        var checkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var check in pluginChecks)
        {
            if (check is null)
            {
                issues.Add(new PluginLoadIssue(
                    assemblyPath,
                    $"Plugin '{plugin.Id}' returned a null check and it was ignored."));
                continue;
            }

            if (!IsValidId(check.Id))
            {
                issues.Add(new PluginLoadIssue(
                    assemblyPath,
                    $"Plugin '{plugin.Id}' returned an invalid check ID. Use 1-64 letters, digits, '.', '_' or '-'."));
                continue;
            }

            if (!checkIds.Add(check.Id))
            {
                issues.Add(new PluginLoadIssue(
                    assemblyPath,
                    $"Plugin '{plugin.Id}' returned duplicate check ID '{check.Id}', which was ignored."));
                continue;
            }

            adaptedChecks.Add(new PluginDiagnosticCheckAdapter(
                plugin.Id,
                string.IsNullOrWhiteSpace(plugin.Name) ? plugin.Id : plugin.Name,
                string.IsNullOrWhiteSpace(plugin.Version) ? "unknown" : plugin.Version,
                check,
                pluginContext));
        }

        plugins.Add(new LoadedPlugin(
            plugin.Id,
            string.IsNullOrWhiteSpace(plugin.Name) ? plugin.Id : plugin.Name,
            string.IsNullOrWhiteSpace(plugin.Version) ? "unknown" : plugin.Version,
            assemblyPath,
            adaptedChecks));
    }

    private static Assembly LoadAssembly(string assemblyPath)
    {
        var existing = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly =>
                !assembly.IsDynamic &&
                !string.IsNullOrWhiteSpace(assembly.Location) &&
                string.Equals(
                    Path.GetFullPath(assembly.Location),
                    assemblyPath,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal));

        if (existing is not null)
        {
            return existing;
        }

        var loadContext = new PluginAssemblyLoadContext(assemblyPath);
        return loadContext.LoadFromAssemblyPath(assemblyPath);
    }

    private static string ResolvePath(string root, string configuredPath) =>
        Path.GetFullPath(
            Path.IsPathFullyQualified(configuredPath)
                ? configuredPath
                : Path.Combine(root, configuredPath));

    private static bool TryGetSetting(
        IReadOnlyDictionary<string, JsonElement> settings,
        string pluginId,
        out JsonElement value)
    {
        foreach (var pair in settings)
        {
            if (string.Equals(pair.Key, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsValidId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || !char.IsLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(ch =>
            char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');
    }

    private sealed class PluginAssemblyLoadContext(string pluginAssemblyPath)
        : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var sdkAssembly = typeof(IErpDoctorPlugin).Assembly;
            if (string.Equals(
                assemblyName.Name,
                sdkAssembly.GetName().Name,
                StringComparison.OrdinalIgnoreCase))
            {
                return sdkAssembly;
            }

            var dependencyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return dependencyPath is null
                ? null
                : LoadFromAssemblyPath(dependencyPath);
        }
    }
}

internal sealed class PluginDiagnosticCheckAdapter(
    string pluginId,
    string pluginName,
    string pluginVersion,
    IPluginCheck check,
    PluginContext pluginContext) : IDiagnosticCheck
{
    public string Id => $"plugin.{pluginId.ToLowerInvariant()}.{check.Id.ToLowerInvariant()}";
    public string Name => $"{pluginName}: {check.Name}";
    public string Category => "plugin";

    public async Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        _ = context;

        PluginDiagnosticResult result;
        try
        {
            result = await check.ExecuteAsync(pluginContext, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Error,
                $"Plugin check failed with {ex.GetType().Name}.",
                new Dictionary<string, string>
                {
                    ["pluginId"] = pluginId,
                    ["pluginVersion"] = pluginVersion,
                    ["pluginCategory"] = check.Category
                },
                [
                    "Inspect the plugin implementation and its configuration.",
                    "ERP Doctor intentionally suppresses raw plugin exception messages because they may contain secrets."
                ]);
        }

        var evidence = new Dictionary<string, string>(
            result.EvidenceOrEmpty,
            StringComparer.OrdinalIgnoreCase)
        {
            ["pluginId"] = pluginId,
            ["pluginVersion"] = pluginVersion,
            ["pluginCategory"] = check.Category
        };

        return new DiagnosticResult(
            Id,
            Name,
            MapStatus(result.Status),
            result.Summary,
            evidence,
            result.SuggestionsOrEmpty);
    }

    private static DiagnosticStatus MapStatus(PluginDiagnosticStatus status) => status switch
    {
        PluginDiagnosticStatus.Healthy => DiagnosticStatus.Healthy,
        PluginDiagnosticStatus.Info => DiagnosticStatus.Info,
        PluginDiagnosticStatus.Warning => DiagnosticStatus.Warning,
        PluginDiagnosticStatus.Critical => DiagnosticStatus.Critical,
        PluginDiagnosticStatus.Skipped => DiagnosticStatus.Skipped,
        PluginDiagnosticStatus.Error => DiagnosticStatus.Error,
        _ => DiagnosticStatus.Error
    };
}

internal sealed class PluginLoadIssueCheck(int sequence, PluginLoadIssue issue) : IDiagnosticCheck
{
    public string Id => $"plugin.load.{sequence}";
    public string Name => "Plugin load";
    public string Category => "plugin";

    public Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new DiagnosticResult(
            Id,
            Name,
            DiagnosticStatus.Error,
            issue.Summary,
            new Dictionary<string, string>
            {
                ["assembly"] = issue.AssemblyPath
            },
            [
                "Verify the configured local DLL path and plugin API version.",
                "Only load plugin assemblies you trust; plugins execute with ERP Doctor process permissions."
            ]));
    }
}
