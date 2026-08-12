using ErpDoctor.PluginHost;

internal static class PluginConsoleReport
{
    public static void Write(PluginDiscovery discovery)
    {
        Console.WriteLine();
        Console.WriteLine("ERP Doctor - Plugins");
        Console.WriteLine(new string('─', 72));

        if (discovery.Plugins.Count == 0)
        {
            Console.WriteLine("No plugins loaded.");
        }
        else
        {
            foreach (var plugin in discovery.Plugins)
            {
                Console.WriteLine($"✓ {plugin.Id}  {plugin.Name}  v{plugin.Version}");
                Console.WriteLine($"  checks   : {plugin.Checks.Count}");
                Console.WriteLine($"  assembly : {plugin.AssemblyPath}");
            }
        }

        if (discovery.Issues.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Load issues");
        Console.WriteLine(new string('─', 72));
        foreach (var issue in discovery.Issues)
        {
            Console.WriteLine($"✗ {issue.Summary}");
            Console.WriteLine($"  assembly : {issue.AssemblyPath}");
        }
    }
}
