using ErpDoctor.Infrastructure.SqlServerDiagnostics;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class SqlGrowthHistoryTests
{
    [Fact]
    public void Compare_CalculatesDatabaseAndTableGrowth()
    {
        var previous = Snapshot(
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            dataMb: 800,
            logMb: 200,
            tables:
            [
                new SqlTableSizeSnapshot("[dbo].[AuditLog]", 1_000, 500),
                new SqlTableSizeSnapshot("[dbo].[Orders]", 500, 100)
            ]);
        var current = Snapshot(
            new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero),
            dataMb: 1_000,
            logMb: 300,
            tables:
            [
                new SqlTableSizeSnapshot("[dbo].[AuditLog]", 1_500, 700),
                new SqlTableSizeSnapshot("[dbo].[Orders]", 500, 100),
                new SqlTableSizeSnapshot("[dbo].[NewTable]", 100, 50)
            ]);

        var comparison = SqlGrowthAnalyzer.Compare(previous, current);

        Assert.Equal(200d, comparison.DataDeltaMb);
        Assert.Equal(100d, comparison.LogDeltaMb);
        Assert.Equal(300d, comparison.TotalDeltaMb);
        Assert.Equal(150d, comparison.TotalGrowthMbPerDay!.Value);
        Assert.Equal(TimeSpan.FromDays(2), comparison.Interval);

        Assert.Equal(2, comparison.TableGrowth.Count);
        var audit = comparison.TableGrowth[0];
        Assert.Equal("[dbo].[AuditLog]", audit.Name);
        Assert.Equal(200d, audit.ReservedDeltaMb);
        Assert.Equal(500L, audit.RowDelta);
        Assert.False(audit.IsNewInCapturedSet);

        var added = comparison.TableGrowth[1];
        Assert.Equal("[dbo].[NewTable]", added.Name);
        Assert.True(added.IsNewInCapturedSet);
        Assert.Equal(50d, added.CurrentReservedMb);
    }

    [Fact]
    public void FindPrevious_UsesLatestSnapshotForSameDatabaseOnly()
    {
        var older = Snapshot(new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        var latest = Snapshot(new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero));
        var otherDatabase = Snapshot(
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
            database: "OTHER_DB");
        var current = Snapshot(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));
        var history = new SqlGrowthHistoryDocument(
            SqlGrowthHistoryDocument.CurrentSchemaVersion,
            [older, latest, otherDatabase]);

        var previous = SqlGrowthAnalyzer.FindPrevious(history, current)
            ?? throw new InvalidOperationException("Expected a matching previous snapshot.");

        Assert.Equal(latest.CapturedAtUtc, previous.CapturedAtUtc);
        Assert.Equal("ERP_PROD", previous.Database);
    }

    [Fact]
    public async Task Store_RoundTripsAndCapsHistoryWithoutConnectionSecrets()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"erp-doctor-growth-{Guid.NewGuid():N}.json");
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            var store = new SqlGrowthHistoryStore();
            var history = SqlGrowthHistoryDocument.Empty;
            for (var day = 1; day <= 4; day++)
            {
                history = store.Append(
                    history,
                    Snapshot(new DateTimeOffset(2026, 8, day, 0, 0, 0, TimeSpan.Zero)),
                    maxSnapshots: 3);
            }

            var savedPath = await store.SaveAsync(path, history, cancellationToken);
            var loaded = await store.LoadAsync(savedPath, cancellationToken);

            Assert.Equal(3, loaded.Snapshots.Count);
            Assert.Equal(2, loaded.Snapshots[0].CapturedAtUtc.Day);
            Assert.Equal(4, loaded.Snapshots[^1].CapturedAtUtc.Day);

            var raw = await File.ReadAllTextAsync(savedPath, cancellationToken);
            Assert.False(raw.Contains("connectionString", StringComparison.OrdinalIgnoreCase));
            Assert.False(raw.Contains("password", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static SqlGrowthSnapshot Snapshot(
        DateTimeOffset capturedAt,
        double dataMb = 800,
        double logMb = 200,
        IReadOnlyList<SqlTableSizeSnapshot>? tables = null,
        string database = "ERP_PROD") =>
        new(
            capturedAt,
            "SQL01",
            database,
            dataMb,
            logMb,
            dataMb + logMb,
            tables ?? Array.Empty<SqlTableSizeSnapshot>());
}
