using System.Diagnostics;
using System.Globalization;
using ErpDoctor.PluginSdk;
using Npgsql;

namespace ErpDoctor.Plugin.Postgres;

public sealed class PostgresPlugin : IErpDoctorPlugin
{
    public string Id => "postgres";
    public string Name => "PostgreSQL Diagnostics";
    public string Version => "0.1.0";

    public IReadOnlyList<IPluginCheck> CreateChecks(PluginContext context)
    {
        var settings = PostgresSettings.From(context.Configuration);
        return
        [
            new PostgresConnectivityCheck(settings),
            new PostgresDatabaseSizeCheck(settings),
            new PostgresLongRunningCheck(settings),
            new PostgresBlockingCheck(settings)
        ];
    }
}

internal abstract class PostgresCheckBase(PostgresSettings settings)
{
    protected PostgresSettings Settings { get; } = settings;

    protected (NpgsqlConnection? Connection, PluginDiagnosticResult? Error) CreateConnection()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(
            Settings.ConnectionStringEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(rawConnectionString))
        {
            return (
                null,
                new PluginDiagnosticResult(
                    PluginDiagnosticStatus.Error,
                    $"Environment variable '{Settings.ConnectionStringEnvironmentVariable}' is not set.",
                    new Dictionary<string, string>
                    {
                        ["connectionStringEnvironmentVariable"] =
                            Settings.ConnectionStringEnvironmentVariable
                    },
                    [
                        $"Set '{Settings.ConnectionStringEnvironmentVariable}' to a PostgreSQL connection string.",
                        "Keep the connection string out of ERP Doctor JSON configuration and diagnostic evidence."
                    ]));
        }

        var builder = new NpgsqlConnectionStringBuilder(rawConnectionString)
        {
            Timeout = Settings.ConnectionTimeoutSeconds,
            CommandTimeout = Settings.CommandTimeoutSeconds,
            ApplicationName = "erp-doctor"
        };

        return (new NpgsqlConnection(builder.ConnectionString), null);
    }

    protected NpgsqlCommand CreateCommand(string sql, NpgsqlConnection connection) =>
        new(sql, connection)
        {
            CommandTimeout = Settings.CommandTimeoutSeconds
        };
}

internal sealed class PostgresConnectivityCheck(PostgresSettings settings)
    : PostgresCheckBase(settings), IPluginCheck
{
    public string Id => "connectivity";
    public string Name => "Connectivity";
    public string Category => "postgres";

    public async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var (connection, error) = CreateConnection();
        if (error is not null)
        {
            return error;
        }

        await using var disposableConnection = connection!;
        var stopwatch = Stopwatch.StartNew();
        await disposableConnection.OpenAsync(cancellationToken);

        await using var command = CreateCommand(
            "SELECT current_database(), current_setting('server_version');",
            disposableConnection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Error,
                "PostgreSQL connection opened but server metadata could not be read.");
        }

        var database = reader.GetString(0);
        var serverVersion = reader.GetString(1);
        stopwatch.Stop();

        return new PluginDiagnosticResult(
            PluginDiagnosticStatus.Healthy,
            $"Connected to PostgreSQL database '{database}' in {stopwatch.ElapsedMilliseconds} ms.",
            new Dictionary<string, string>
            {
                ["database"] = database,
                ["serverVersion"] = serverVersion,
                ["latencyMs"] = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
            });
    }
}

internal sealed class PostgresDatabaseSizeCheck(PostgresSettings settings)
    : PostgresCheckBase(settings), IPluginCheck
{
    public string Id => "database-size";
    public string Name => "Database size";
    public string Category => "postgres";

    public async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var (connection, error) = CreateConnection();
        if (error is not null)
        {
            return error;
        }

        await using var disposableConnection = connection!;
        await disposableConnection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(
            "SELECT pg_database_size(current_database())::bigint;",
            disposableConnection);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not long sizeBytes)
        {
            return new PluginDiagnosticResult(
                PluginDiagnosticStatus.Error,
                "PostgreSQL database size query returned an unexpected result.");
        }

        return PostgresDatabaseSizeEvaluator.Evaluate(
            sizeBytes,
            Settings.DatabaseSizeWarningGb);
    }
}

internal sealed class PostgresLongRunningCheck(PostgresSettings settings)
    : PostgresCheckBase(settings), IPluginCheck
{
    public string Id => "long-running";
    public string Name => "Long-running queries";
    public string Category => "postgres";

    public async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var (connection, error) = CreateConnection();
        if (error is not null)
        {
            return error;
        }

        await using var disposableConnection = connection!;
        await disposableConnection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(
            """
            SELECT
                pid,
                EXTRACT(EPOCH FROM (clock_timestamp() - query_start))::double precision AS duration_seconds
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND state = 'active'
              AND pid <> pg_backend_pid()
              AND query_start IS NOT NULL
              AND EXTRACT(EPOCH FROM (clock_timestamp() - query_start)) >= @threshold_seconds
            ORDER BY query_start
            LIMIT 50;
            """,
            disposableConnection);
        command.Parameters.AddWithValue(
            "threshold_seconds",
            Settings.LongRunningWarningSeconds);

        var snapshots = new List<PostgresLongRunningSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            snapshots.Add(new PostgresLongRunningSnapshot(
                reader.GetInt32(0),
                reader.GetDouble(1)));
        }

        return PostgresLongRunningEvaluator.Evaluate(
            snapshots,
            Settings.LongRunningWarningSeconds);
    }
}

internal sealed class PostgresBlockingCheck(PostgresSettings settings)
    : PostgresCheckBase(settings), IPluginCheck
{
    public string Id => "blocking";
    public string Name => "Blocking sessions";
    public string Category => "postgres";

    public async Task<PluginDiagnosticResult> ExecuteAsync(
        PluginContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var (connection, error) = CreateConnection();
        if (error is not null)
        {
            return error;
        }

        await using var disposableConnection = connection!;
        await disposableConnection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(
            """
            SELECT
                pid,
                pg_blocking_pids(pid),
                EXTRACT(
                    EPOCH FROM (
                        clock_timestamp() - COALESCE(query_start, xact_start, backend_start)
                    )
                )::double precision AS blocked_seconds,
                COALESCE(wait_event_type || ':' || wait_event, 'unknown') AS wait_event
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND cardinality(pg_blocking_pids(pid)) > 0
              AND EXTRACT(
                    EPOCH FROM (
                        clock_timestamp() - COALESCE(query_start, xact_start, backend_start)
                    )
                  ) >= @threshold_seconds
            ORDER BY blocked_seconds DESC
            LIMIT 50;
            """,
            disposableConnection);
        command.Parameters.AddWithValue(
            "threshold_seconds",
            Settings.BlockingWarningSeconds);

        var snapshots = new List<PostgresBlockingSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            snapshots.Add(new PostgresBlockingSnapshot(
                reader.GetInt32(0),
                reader.GetFieldValue<int[]>(1),
                reader.GetDouble(2),
                reader.GetString(3)));
        }

        return PostgresBlockingEvaluator.Evaluate(
            snapshots,
            Settings.BlockingWarningSeconds);
    }
}
