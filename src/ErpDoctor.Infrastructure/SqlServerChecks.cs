using System.Data;
using System.Diagnostics;
using ErpDoctor.Core;
using Microsoft.Data.SqlClient;

namespace ErpDoctor.Infrastructure.SqlServerDiagnostics;

internal static class SqlConnectionFactory
{
    public static bool HasConnectionString(DiagnosticContext context) =>
        !string.IsNullOrWhiteSpace(context.Options.SqlServer.ConnectionString);

    public static SqlConnection Create(DiagnosticContext context) =>
        new(context.Options.SqlServer.ConnectionString!);
}

public sealed class SqlConnectivityCheck : IDiagnosticCheck
{
    public string Id => "sql.connection";
    public string Name => "SQL Server connection";
    public string Category => "sql";

    public async Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        if (!SqlConnectionFactory.HasConnectionString(context))
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Skipped,
                "No SQL Server connection string configured.");
        }

        await using var connection = SqlConnectionFactory.Create(context);
        var stopwatch = Stopwatch.StartNew();
        await connection.OpenAsync(cancellationToken);
        stopwatch.Stop();

        await using var command = new SqlCommand(
            "SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)), DB_NAME();",
            connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        var server = reader.IsDBNull(0) ? "(unknown)" : reader.GetString(0);
        var database = reader.IsDBNull(1) ? "(unknown)" : reader.GetString(1);

        return new DiagnosticResult(
            Id,
            Name,
            DiagnosticStatus.Healthy,
            $"Connected to {server}/{database} in {stopwatch.ElapsedMilliseconds} ms",
            new Dictionary<string, string>
            {
                ["server"] = server,
                ["database"] = database,
                ["latencyMs"] = stopwatch.ElapsedMilliseconds.ToString()
            });
    }
}

public sealed class SqlDatabaseSizeCheck : IDiagnosticCheck
{
    public string Id => "sql.database-size";
    public string Name => "SQL Server database size";
    public string Category => "sql";

    public async Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        if (!SqlConnectionFactory.HasConnectionString(context))
        {
            return new DiagnosticResult(Id, Name, DiagnosticStatus.Skipped, "No SQL Server connection string configured.");
        }

        const string sql = """
            SELECT
                DB_NAME() AS DatabaseName,
                SUM(CASE WHEN type_desc = 'ROWS' THEN size ELSE 0 END) / 128.0 AS DataSizeMb,
                SUM(CASE WHEN type_desc = 'LOG' THEN size ELSE 0 END) / 128.0 AS LogSizeMb,
                SUM(size) / 128.0 AS TotalSizeMb
            FROM sys.database_files;
            """;

        await using var connection = SqlConnectionFactory.Create(context);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        var database = reader.GetString(0);
        var dataMb = Convert.ToDouble(reader.GetValue(1));
        var logMb = Convert.ToDouble(reader.GetValue(2));
        var totalMb = Convert.ToDouble(reader.GetValue(3));
        var totalGb = totalMb / 1024d;
        var logGb = logMb / 1024d;

        var warning = totalGb >= context.Options.SqlServer.DatabaseSizeWarningGb ||
                      logGb >= context.Options.SqlServer.LogSizeWarningGb;

        return new DiagnosticResult(
            Id,
            Name,
            warning ? DiagnosticStatus.Warning : DiagnosticStatus.Healthy,
            $"{database}: {totalGb:F2} GB total ({dataMb / 1024d:F2} GB data, {logGb:F2} GB log)",
            new Dictionary<string, string>
            {
                ["database"] = database,
                ["dataSizeMb"] = dataMb.ToString("F2"),
                ["logSizeMb"] = logMb.ToString("F2"),
                ["totalSizeMb"] = totalMb.ToString("F2")
            },
            warning
                ? [
                    "Large size alone is not an error. Compare growth over time before taking action.",
                    "Do not shrink database files automatically; inspect the growth source and log reuse state first."
                ]
                : null);
    }
}

public sealed class SqlLargestTablesCheck : IDiagnosticCheck
{
    public string Id => "sql.largest-tables";
    public string Name => "SQL Server largest tables";
    public string Category => "sql";

    public async Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        if (!SqlConnectionFactory.HasConnectionString(context))
        {
            return new DiagnosticResult(Id, Name, DiagnosticStatus.Skipped, "No SQL Server connection string configured.");
        }

        const string sql = """
            SELECT TOP (@Limit)
                QUOTENAME(s.name) + '.' + QUOTENAME(t.name) AS TableName,
                SUM(ps.row_count) AS RowCount,
                SUM(ps.reserved_page_count) * 8.0 / 1024.0 AS ReservedMb
            FROM sys.dm_db_partition_stats ps
            INNER JOIN sys.tables t ON ps.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE ps.index_id IN (0, 1)
            GROUP BY s.name, t.name
            ORDER BY ReservedMb DESC;
            """;

        await using var connection = SqlConnectionFactory.Create(context);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Limit", SqlDbType.Int).Value =
            Math.Clamp(context.Options.SqlServer.LargestTablesLimit, 1, 50);

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var rows = Convert.ToInt64(reader.GetValue(1));
            var mb = Convert.ToDouble(reader.GetValue(2));
            tables.Add($"{name}: {rows:N0} rows, {mb:F1} MB");
        }

        return new DiagnosticResult(
            Id,
            Name,
            DiagnosticStatus.Info,
            tables.Count == 0
                ? "No user table size information returned."
                : $"Top table: {tables[0]}",
            tables.Select((value, index) =>
                    new KeyValuePair<string, string>($"table{index + 1}", value))
                .ToDictionary());
    }
}

public sealed class SqlBlockingCheck : IDiagnosticCheck
{
    public string Id => "sql.blocking";
    public string Name => "SQL Server blocking";
    public string Category => "sql";

    public async Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        if (!SqlConnectionFactory.HasConnectionString(context))
        {
            return new DiagnosticResult(Id, Name, DiagnosticStatus.Skipped, "No SQL Server connection string configured.");
        }

        const string sql = """
            SELECT
                session_id,
                blocking_session_id,
                wait_type,
                wait_time,
                wait_resource
            FROM sys.dm_exec_requests
            WHERE blocking_session_id <> 0
            ORDER BY wait_time DESC;
            """;

        await using var connection = SqlConnectionFactory.Create(context);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var blocked = new List<(int Session, int Blocker, string WaitType, int WaitMs, string Resource)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            blocked.Add((
                reader.GetInt16(0),
                reader.GetInt16(1),
                reader.IsDBNull(2) ? "(none)" : reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? "(none)" : reader.GetString(4)));
        }

        if (blocked.Count == 0)
        {
            return new DiagnosticResult(
                Id,
                Name,
                DiagnosticStatus.Healthy,
                "No blocked requests detected.");
        }

        var worst = blocked.MaxBy(x => x.WaitMs);
        var thresholdMs = context.Options.SqlServer.BlockingWarningSeconds * 1000;
        var status = worst.WaitMs >= thresholdMs
            ? DiagnosticStatus.Warning
            : DiagnosticStatus.Info;

        return new DiagnosticResult(
            Id,
            Name,
            status,
            $"{blocked.Count} blocked request(s); longest wait {worst.WaitMs / 1000d:F1}s (session {worst.Session} blocked by {worst.Blocker})",
            blocked.Select((item, index) =>
                new KeyValuePair<string, string>(
                    $"blocked{index + 1}",
                    $"session={item.Session}; blocker={item.Blocker}; wait={item.WaitType}; waitMs={item.WaitMs}; resource={item.Resource}"))
                .ToDictionary(),
            status == DiagnosticStatus.Warning
                ? [
                    "Identify the owning transaction and application before taking action.",
                    "ERP Doctor intentionally does not kill SQL sessions."
                ]
                : null);
    }
}

public sealed class SqlLongRunningRequestsCheck : IDiagnosticCheck
{
    public string Id => "sql.long-running";
    public string Name => "SQL Server long-running requests";
    public string Category => "sql";

    public async Task<DiagnosticResult> ExecuteAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        if (!SqlConnectionFactory.HasConnectionString(context))
        {
            return new DiagnosticResult(Id, Name, DiagnosticStatus.Skipped, "No SQL Server connection string configured.");
        }

        const string sql = """
            SELECT
                session_id,
                DATEDIFF(SECOND, start_time, SYSDATETIME()) AS ElapsedSeconds,
                status,
                command,
                wait_type
            FROM sys.dm_exec_requests
            WHERE session_id <> @@SPID
              AND DATEDIFF(SECOND, start_time, SYSDATETIME()) >= @ThresholdSeconds
            ORDER BY ElapsedSeconds DESC;
            """;

        await using var connection = SqlConnectionFactory.Create(context);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ThresholdSeconds", SqlDbType.Int).Value =
            Math.Max(1, context.Options.SqlServer.LongRunningWarningSeconds);

        var requests = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(
                $"session={reader.GetInt16(0)}; elapsed={reader.GetInt32(1)}s; status={reader.GetString(2)}; command={reader.GetString(3)}; wait={((reader.IsDBNull(4)) ? "(none)" : reader.GetString(4))}");
        }

        return new DiagnosticResult(
            Id,
            Name,
            requests.Count == 0 ? DiagnosticStatus.Healthy : DiagnosticStatus.Warning,
            requests.Count == 0
                ? $"No requests running longer than {context.Options.SqlServer.LongRunningWarningSeconds}s."
                : $"{requests.Count} request(s) exceed {context.Options.SqlServer.LongRunningWarningSeconds}s.",
            requests.Select((value, index) =>
                    new KeyValuePair<string, string>($"request{index + 1}", value))
                .ToDictionary(),
            requests.Count > 0
                ? ["Review the query and transaction context; elapsed time alone does not prove a query is unhealthy."]
                : null);
    }
}
