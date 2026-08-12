using System.Text.Json;
using ErpDoctor.Core;
using ErpDoctor.Plugin.Postgres;
using ErpDoctor.PluginHost;
using ErpDoctor.PluginSdk;
using Xunit;
using PostgresPluginType = ErpDoctor.Plugin.Postgres.PostgresPlugin;

namespace ErpDoctor.Tests;

public sealed class PostgresPluginTests
{
    [Fact]
    public void Settings_AreBoundedAndEnvironmentVariableNameIsTrimmed()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "connectionStringEnvironmentVariable": "  PG_TEST_CONN  ",
              "connectionTimeoutSeconds": 0,
              "commandTimeoutSeconds": 999,
              "databaseSizeWarningGb": -5,
              "longRunningWarningSeconds": 0,
              "blockingWarningSeconds": 999999
            }
            """);

        var settings = PostgresSettings.From(document.RootElement);

        Assert.Equal("PG_TEST_CONN", settings.ConnectionStringEnvironmentVariable);
        Assert.Equal(1, settings.ConnectionTimeoutSeconds);
        Assert.Equal(60, settings.CommandTimeoutSeconds);
        Assert.Equal(0.1d, settings.DatabaseSizeWarningGb);
        Assert.Equal(1, settings.LongRunningWarningSeconds);
        Assert.Equal(86_400, settings.BlockingWarningSeconds);
    }

    [Fact]
    public void Plugin_RegistersFourReadOnlyDiagnosticChecks()
    {
        var plugin = new PostgresPluginType();

        var checks = plugin.CreateChecks(
            new PluginContext(null, Environment.CurrentDirectory));

        Assert.Equal(4, checks.Count);
        Assert.Contains(checks, check => check.Id == "connectivity");
        Assert.Contains(checks, check => check.Id == "database-size");
        Assert.Contains(checks, check => check.Id == "long-running");
        Assert.Contains(checks, check => check.Id == "blocking");
        Assert.All(checks, check => Assert.Equal("postgres", check.Category));
    }

    [Fact]
    public void PluginHost_DiscoversAndNamespacesPostgreSqlChecks()
    {
        var options = new PluginOptions
        {
            Assemblies = [typeof(PostgresPluginType).Assembly.Location]
        };

        var discovery = new PluginLoader().Load(options, Environment.CurrentDirectory);

        var plugin = Assert.Single(discovery.Plugins);
        Assert.Empty(discovery.Issues);
        Assert.Equal("postgres", plugin.Id);
        Assert.Equal(4, plugin.Checks.Count);
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.postgres.connectivity");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.postgres.database-size");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.postgres.long-running");
        Assert.Contains(plugin.Checks, check => check.Id == "plugin.postgres.blocking");
        Assert.All(plugin.Checks, check => Assert.Equal("plugin", check.Category));
    }

    [Fact]
    public async Task Connectivity_MissingSecretReportsEnvironmentVariableNameOnly()
    {
        const string variableName = "ERP_DOCTOR_POSTGRES_TEST_MISSING";
        var previous = Environment.GetEnvironmentVariable(variableName);

        try
        {
            Environment.SetEnvironmentVariable(variableName, null);
            using var document = JsonDocument.Parse(
                $$"""
                {
                  "connectionStringEnvironmentVariable": "{{variableName}}"
                }
                """);
            var plugin = new PostgresPluginType();
            var check = plugin.CreateChecks(
                    new PluginContext(document.RootElement.Clone(), Environment.CurrentDirectory))
                .Single(item => item.Id == "connectivity");

            var result = await check.ExecuteAsync(
                new PluginContext(document.RootElement.Clone(), Environment.CurrentDirectory),
                TestContext.Current.CancellationToken);

            Assert.Equal(PluginDiagnosticStatus.Error, result.Status);
            Assert.Contains(variableName, result.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("Host=", result.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Password=", result.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
        }
    }

    [Fact]
    public void DatabaseSizeEvaluator_WarnsAtThreshold()
    {
        const long sizeBytes = 25L * 1024 * 1024 * 1024;

        var result = PostgresDatabaseSizeEvaluator.Evaluate(sizeBytes, 20d);

        Assert.Equal(PluginDiagnosticStatus.Warning, result.Status);
        Assert.Equal("25.00", result.EvidenceOrEmpty["databaseSizeGb"]);
        Assert.Contains("20.00 GB", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void LongRunningEvaluator_ReportsPidsWithoutSqlText()
    {
        var snapshots = new[]
        {
            new PostgresLongRunningSnapshot(101, 42.5),
            new PostgresLongRunningSnapshot(202, 75.2)
        };

        var result = PostgresLongRunningEvaluator.Evaluate(snapshots, 30);
        var rendered = string.Join("\n", result.EvidenceOrEmpty.Values);

        Assert.Equal(PluginDiagnosticStatus.Warning, result.Status);
        Assert.Contains("101", rendered, StringComparison.Ordinal);
        Assert.Contains("202", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("queryText", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlockingEvaluator_ReportsBlockedAndBlockingPids()
    {
        var snapshots = new[]
        {
            new PostgresBlockingSnapshot(
                301,
                new[] { 401, 402 },
                18.4,
                "Lock:transactionid")
        };

        var result = PostgresBlockingEvaluator.Evaluate(snapshots, 10);

        Assert.Equal(PluginDiagnosticStatus.Warning, result.Status);
        Assert.Contains("pid=301", result.EvidenceOrEmpty["blocked1"], StringComparison.Ordinal);
        Assert.Contains("401,402", result.EvidenceOrEmpty["blocked1"], StringComparison.Ordinal);
        Assert.Contains("Lock:transactionid", result.EvidenceOrEmpty["blocked1"], StringComparison.Ordinal);
    }
}
