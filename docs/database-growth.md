# Database growth history

ERP Doctor v0.4 can answer a question that a one-time size check cannot: **what grew since the last time I checked?**

```bash
erp-doctor growth --config erp-doctor.json
```

The first run creates a local baseline. Later runs compare the current SQL Server snapshot with the most recent snapshot for the same server and database.

## What is captured

Each snapshot stores only troubleshooting metadata:

- capture timestamp in UTC,
- SQL Server name,
- database name,
- data-file size,
- log-file size,
- total database size,
- row count and reserved size for the largest captured tables.

The SQL connection string and credentials are **not** written to the history file.

## Local history

The default history path is:

```text
erp-doctor-growth.json
```

Choose another path with:

```bash
erp-doctor growth --config erp-doctor.json --history D:\erp-doctor\customer-a-growth.json
```

History is written with an atomic temporary-file replacement and capped to the most recent 500 snapshots by default.

ERP Doctor does **not** create a history table, trigger, SQL Agent job, stored procedure, or any other object in the ERP database.

## Comparison output

When a previous baseline exists, ERP Doctor reports:

- data-file delta in MB,
- log-file delta in MB,
- total database delta in MB,
- MB/day rate when snapshots are at least one hour apart,
- top table-size and row-count changes,
- tables that are new to the captured top-table set.

A table marked `new in captured set` is not automatically treated as having grown from zero. It may simply have entered the configured top-N capture window.

## Capture depth

`sqlServer.growthTablesLimit` controls how many of the largest tables are stored per snapshot. The default is 50 and the accepted runtime range is 1-500.

```json
{
  "sqlServer": {
    "connectionString": "${ERP_DB}",
    "growthTablesLimit": 50
  }
}
```

## Safety boundary

SQL access remains read-only. The only write performed by `growth` is ERP Doctor's own local JSON history file.

The history file may contain infrastructure and schema information such as server names, database names, and table names. Treat it as internal diagnostic data when sharing it.
