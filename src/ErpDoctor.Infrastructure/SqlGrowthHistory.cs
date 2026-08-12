using System.Data;
using System.Text.Json;
using ErpDoctor.Core;
using Microsoft.Data.SqlClient;

namespace ErpDoctor.Infrastructure.SqlServerDiagnostics;

public sealed record SqlTableSizeSnapshot(
    string Name,
    long RowCount,
    double ReservedMb);

public sealed record SqlGrowthSnapshot(
    DateTimeOffset CapturedAtUtc,
    string Server,
    string Database,
    double DataSizeMb,
    double LogSizeMb,
    double TotalSizeMb,
    IReadOnlyList<SqlTableSizeSnapshot> Tables);

public sealed record SqlTableGrowth(
    string Name,
    long PreviousRowCount,
    long CurrentRowCount,
    long RowDelta,
    double PreviousReservedMb,
    double CurrentReservedMb,
    double ReservedDeltaMb,
    bool IsNewInCapturedSet);

public sealed record SqlGrowthComparison(
    DateTimeOffset PreviousCapturedAtUtc,
    DateTimeOffset CurrentCapturedAtUtc,
    TimeSpan Interval,
    double DataDeltaMb,
    double LogDeltaMb,
    double TotalDeltaMb,
    double? TotalGrowthMbPerDay,
    IReadOnlyList<SqlTableGrowth> TableGrowth);

public sealed record SqlGrowthHistoryDocument(
    string SchemaVersion,
    IReadOnlyList<SqlGrowthSnapshot> Snapshots)
{
    public const string CurrentSchemaVersion = "1.0";

    public static SqlGrowthHistoryDocument Empty { get; } =
        new(CurrentSchemaVersion, Array.Empty<SqlGrowthSnapshot>());
}

public sealed class SqlGrowthSnapshotCollector
{
    public async Task<SqlGrowthSnapshot> CaptureAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken)
    {
        if (!SqlConnectionFactory.HasConnectionString(context))
        {
            throw new InvalidOperationException(
                "No SQL Server connection string configured. Set sqlServer.connectionString or its environment variable.");
        }

        await using var connection = SqlConnectionFactory.Create(context);
        await connection.OpenAsync(cancellationToken);

        var (server, database, dataMb, logMb, totalMb) =
            await ReadDatabaseSizeAsync(connection, cancellationToken);
        var tables = await ReadTableSizesAsync(
            connection,
            Math.Clamp(context.Options.SqlServer.GrowthTablesLimit, 1, 500),
            cancellationToken);

        return new SqlGrowthSnapshot(
            DateTimeOffset.UtcNow,
            server,
            database,
            dataMb,
            logMb,
            totalMb,
            tables);
    }

    private static async Task<(string Server, string Database, double DataMb, double LogMb, double TotalMb)>
        ReadDatabaseSizeAsync(
            SqlConnection connection,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)) AS ServerName,
                DB_NAME() AS DatabaseName,
                SUM(CASE WHEN type_desc = 'ROWS' THEN size ELSE 0 END) / 128.0 AS DataSizeMb,
                SUM(CASE WHEN type_desc = 'LOG' THEN size ELSE 0 END) / 128.0 AS LogSizeMb,
                SUM(size) / 128.0 AS TotalSizeMb
            FROM sys.database_files;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("SQL Server returned no database size information.");
        }

        var server = reader.IsDBNull(0) ? "(unknown)" : reader.GetString(0);
        var database = reader.IsDBNull(1) ? "(unknown)" : reader.GetString(1);
        var dataMb = reader.IsDBNull(2) ? 0d : Convert.ToDouble(reader.GetValue(2));
        var logMb = reader.IsDBNull(3) ? 0d : Convert.ToDouble(reader.GetValue(3));
        var totalMb = reader.IsDBNull(4) ? dataMb + logMb : Convert.ToDouble(reader.GetValue(4));
        return (server, database, dataMb, logMb, totalMb);
    }

    private static async Task<IReadOnlyList<SqlTableSizeSnapshot>> ReadTableSizesAsync(
        SqlConnection connection,
        int limit,
        CancellationToken cancellationToken)
    {
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

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = limit;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var tables = new List<SqlTableSizeSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(new SqlTableSizeSnapshot(
                reader.GetString(0),
                Convert.ToInt64(reader.GetValue(1)),
                Convert.ToDouble(reader.GetValue(2))));
        }

        return tables;
    }
}

public static class SqlGrowthAnalyzer
{
    public static SqlGrowthSnapshot? FindPrevious(
        SqlGrowthHistoryDocument history,
        SqlGrowthSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(current);

        return history.Snapshots
            .Where(snapshot =>
                string.Equals(snapshot.Server, current.Server, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(snapshot.Database, current.Database, StringComparison.OrdinalIgnoreCase) &&
                snapshot.CapturedAtUtc < current.CapturedAtUtc)
            .OrderByDescending(snapshot => snapshot.CapturedAtUtc)
            .FirstOrDefault();
    }

    public static SqlGrowthComparison Compare(
        SqlGrowthSnapshot previous,
        SqlGrowthSnapshot current,
        int tableLimit = 10)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        if (!string.Equals(previous.Server, current.Server, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previous.Database, current.Database, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Growth snapshots must belong to the same SQL Server database.");
        }

        var interval = current.CapturedAtUtc - previous.CapturedAtUtc;
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentException("The current growth snapshot must be newer than the previous snapshot.");
        }

        var previousTables = previous.Tables.ToDictionary(
            table => table.Name,
            StringComparer.OrdinalIgnoreCase);

        var tableGrowth = current.Tables
            .Select(table =>
            {
                var exists = previousTables.TryGetValue(table.Name, out var old);
                return new SqlTableGrowth(
                    table.Name,
                    old?.RowCount ?? 0,
                    table.RowCount,
                    exists ? table.RowCount - old!.RowCount : 0,
                    old?.ReservedMb ?? 0,
                    table.ReservedMb,
                    exists ? table.ReservedMb - old!.ReservedMb : 0,
                    !exists);
            })
            .Where(item =>
                item.IsNewInCapturedSet ||
                Math.Abs(item.ReservedDeltaMb) >= 0.05 ||
                item.RowDelta != 0)
            .OrderByDescending(item =>
                item.IsNewInCapturedSet ? item.CurrentReservedMb : item.ReservedDeltaMb)
            .ThenByDescending(item => item.CurrentReservedMb)
            .Take(Math.Clamp(tableLimit, 1, 100))
            .ToArray();

        var totalDeltaMb = current.TotalSizeMb - previous.TotalSizeMb;
        var growthPerDay = interval.TotalHours >= 1
            ? totalDeltaMb / interval.TotalDays
            : null;

        return new SqlGrowthComparison(
            previous.CapturedAtUtc,
            current.CapturedAtUtc,
            interval,
            current.DataSizeMb - previous.DataSizeMb,
            current.LogSizeMb - previous.LogSizeMb,
            totalDeltaMb,
            growthPerDay,
            tableGrowth);
    }
}

public sealed class SqlGrowthHistoryStore
{
    private const int DefaultMaxSnapshots = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<SqlGrowthHistoryDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return SqlGrowthHistoryDocument.Empty;
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var document = await JsonSerializer.DeserializeAsync<SqlGrowthHistoryDocument>(
            stream,
            JsonOptions,
            cancellationToken);

        if (document is null ||
            !string.Equals(
                document.SchemaVersion,
                SqlGrowthHistoryDocument.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported or invalid growth history file: {fullPath}");
        }

        return document;
    }

    public SqlGrowthHistoryDocument Append(
        SqlGrowthHistoryDocument history,
        SqlGrowthSnapshot snapshot,
        int maxSnapshots = DefaultMaxSnapshots)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(snapshot);

        var limit = Math.Clamp(maxSnapshots, 2, 5000);
        var snapshots = history.Snapshots
            .Append(snapshot)
            .OrderBy(item => item.CapturedAtUtc)
            .TakeLast(limit)
            .ToArray();

        return new SqlGrowthHistoryDocument(
            SqlGrowthHistoryDocument.CurrentSchemaVersion,
            snapshots);
    }

    public async Task<string> SaveAsync(
        string path,
        SqlGrowthHistoryDocument history,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(history);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    history,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            return fullPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
