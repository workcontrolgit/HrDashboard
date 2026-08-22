# HrDashboard — Provider-Agnostic HR Data + Chat-With-Your-Data Design

**Date:** 2026-08-22
**Status:** Approved

## Goal

Make the HR data backend switchable between Oracle and SQL Server via `appsettings` config, and add a "chat with your data" path — generic schema-discovery tools plus a guarded ad hoc SQL tool — so the agent can answer questions the curated tools don't cover, without needing a code change per new question.

## Scope

- Replace the SQLcl-MCP-subprocess connection to Oracle with a plain ADO.NET connection (`Oracle.ManagedDataAccess.Core`), matching the connection-string config style already used for the app DB.
- Add a `SqlServerBridge` (`Microsoft.Data.SqlClient`) as a second provider, selected the same way.
- Add schema-discovery MCP tools (`ListTables`, `DescribeTable`) shared across both providers.
- Add a one-time clone/ETL console tool that copies the Oracle HR schema (`EMPLOYEES`, `DEPARTMENTS`, `JOBS`, `LOCATIONS`) into a new SQL Server database on the existing LocalDB instance, for end-to-end testing of the SQL Server path.
- Out of scope: running both providers simultaneously; a generic Oracle-DDL-to-T-SQL translator (the clone uses hand-written T-SQL since the source schema is small and fixed); a real-time/production ETL sync (the clone tool is rerunnable but not scheduled).

## Why drop SQLcl/MCP for Oracle

The current `OracleBridge` shells out to the SQLcl CLI in `-mcp` mode and calls a `connect` tool with a *saved connection name* (`hr_local`), whose credentials live in SQLcl's local connection store. That's fine for local dev but doesn't translate to a server/container/CI environment — there's no straightforward way to provision that saved connection non-interactively. Switching both providers to standard ADO.NET connection strings (`Oracle.ManagedDataAccess.Core` for Oracle, `Microsoft.Data.SqlClient` for SQL Server) removes the subprocess and the saved-connection dependency entirely, and lets credentials flow through normal .NET config (env vars, user-secrets, Key Vault) exactly like `ConnectionStrings:DefaultConnection` already does for the app database.

## Architecture

### `IHrDataBridge` (renamed from `IOracleBridge`)

```csharp
namespace HrDashboard.McpServer;

public interface IHrDataBridge
{
    Task<string> RunSqlAsync(string sql, CancellationToken ct = default);
    Task<string> ListTablesAsync(CancellationToken ct = default);
    Task<string> DescribeTableAsync(string tableName, CancellationToken ct = default);
}
```

All three methods return JSON text (matching the existing `RunSqlAsync` convention), so tool methods stay thin pass-throughs.

### `OracleBridge : IHrDataBridge`

- Opens an `OracleConnection` per call (or a small pool) using `ConnectionStrings:HrData`.
- No longer an `IHostedService` — no subprocess to start/stop.
- `ListTablesAsync` queries `ALL_TAB_COLUMNS`/`USER_TABLES` scoped to the connected schema.
- `DescribeTableAsync` queries `ALL_TAB_COLUMNS` filtered by table name.
- Catches connection/SQL exceptions and returns `"[SQL error: ...]"` text (existing pattern), never throws across the MCP boundary.

### `SqlServerBridge : IHrDataBridge`

- Same shape, using `SqlConnection` against `ConnectionStrings:HrData`.
- `ListTablesAsync` queries `INFORMATION_SCHEMA.TABLES`.
- `DescribeTableAsync` queries `INFORMATION_SCHEMA.COLUMNS` filtered by table name.
- Same error-wrapping convention as `OracleBridge`.

### Config

```json
{
  "Database": { "Provider": "Oracle" },
  "ConnectionStrings": {
    "HrData": "User Id=hr;Password=...;Data Source=localhost:1521/XEPDB1"
  }
}
```

For SQL Server: `"Provider": "SqlServer"` and a standard SQL Server connection string pointing at the new `HrData` database on `(localdb)\mssqllocaldb`.

`Program.cs` reads `Database:Provider`, registers exactly one `IHrDataBridge` implementation (no hosted service for either provider anymore), and registers the matching provider-specific analytics tool class:

```csharp
var provider = config["Database:Provider"] ?? "Oracle";
if (provider == "SqlServer")
{
    services.AddSingleton<IHrDataBridge, SqlServerBridge>();
    mcpBuilder.WithTools<SqlServerAnalyticsTools>();
}
else
{
    services.AddSingleton<IHrDataBridge, OracleBridge>();
    mcpBuilder.WithTools<OracleAnalyticsTools>();
}
mcpBuilder.WithTools<HrSchemaTools>();
```

Startup throws `InvalidOperationException` on an unrecognized `Database:Provider` value or a missing `ConnectionStrings:HrData`, matching the existing fail-fast pattern in `HrDashboard.Web/Program.cs`.

## Tool Layer

### Curated tools — two parallel classes, same MCP tool names

`OracleAnalyticsTools` (renamed from `HrAnalyticsTools`, unchanged SQL) and a new `SqlServerAnalyticsTools`, both exposing identical `[McpServerTool(Name = ...)]` names and descriptions:

- `GetTopEarnersByDepartment`
- `GetSalaryBreakdownByDepartment`
- `GetDeptHeadcount`
- `GetJobSalaryRanges`
- `RunHrQuery` (ad hoc, see guardrails below)

Only the embedded SQL differs (T-SQL `FOR JSON`/`ISNULL`/`TOP` vs Oracle `JSON_OBJECT`/`NVL`/`FETCH FIRST`). The agent-facing tool surface is identical regardless of provider, so `HrAgentService` and the chat UI need no changes.

### Schema-discovery tools — one shared class

`HrSchemaTools(IHrDataBridge bridge)`, registered regardless of provider:

- `ListTables` → `bridge.ListTablesAsync()`
- `DescribeTable(tableName)` → `bridge.DescribeTableAsync(tableName)`

These are dialect-agnostic at the tool level since each bridge implements the catalog query internally.

### Guardrails for the ad hoc path (`RunHrQuery`)

1. Keep the existing SELECT-only prefix check (reject anything else without calling the bridge).
2. DB-level scoped credentials are the real enforcement boundary: Oracle's `hr` user is already schema-scoped; the new SQL Server login is granted SELECT only on the 4 HR tables in the `HrData` database (not `db_owner`, not access to the app's `HrDashboard` database).
3. Both bridges set an ADO.NET `CommandTimeout` (10s) to bound a runaway ad hoc query.
4. Both bridges truncate returned rows to a cap (200) before returning JSON text to the agent, so a broad ad hoc query can't flood the context window.

No SQL parser or app-level table allowlist — the DB permission boundary already does this job and is more robust than string-matching generated SQL.

## Oracle → SQL Server Clone (test data)

**Target:** a new `HrData` database on the same `(localdb)\mssqllocaldb` instance that already hosts the app's `HrDashboard` database — kept separate, mirroring today's separation between HR data and app data.

**Schema:** hand-written T-SQL `CREATE TABLE` statements for the 4 known tables (`EMPLOYEES`, `DEPARTMENTS`, `JOBS`, `LOCATIONS` — the fixed, well-known Oracle HR sample schema), with manual type mapping:

| Oracle | SQL Server |
|---|---|
| `NUMBER` (integer-valued) | `INT` |
| `NUMBER(p,s)` | `DECIMAL(p,s)` |
| `VARCHAR2(n)` | `NVARCHAR(n)` |
| `DATE` | `DATETIME2` |

A generic Oracle-DDL-introspection-to-T-SQL translator was considered and rejected — for 4 fixed tables, hand-mapping is simpler and avoids precision/scale edge cases a generic translator would need to handle for no real reuse benefit.

**Data copy:** standalone console tool at `tools/HrDataClone/Program.cs`:
1. Connects to Oracle via `Oracle.ManagedDataAccess.Core`, reads all rows from the 4 tables.
2. Truncates the corresponding SQL Server tables.
3. Bulk-inserts via `SqlBulkCopy`.

Rerunnable on demand to refresh the clone after Oracle data changes. Not part of the production app; a dev/test utility only.

## Error Handling

- Both bridges catch connection/SQL exceptions and return `"[SQL error: ...]"` (or a bridge-unavailable message) as text, never throwing across the MCP boundary — preserves the existing retry-friendly pattern where the agent can read an error and adjust its next SQL.
- Startup fails fast (`InvalidOperationException`) on an unrecognized `Database:Provider` or missing `ConnectionStrings:HrData`.
- `ListTablesAsync`/`DescribeTableAsync` return an empty result / clear message on a permissions error rather than throwing.

## Testing

- Rename `FakeOracleBridge` → `FakeHrDataBridge` (implements the widened interface) in `tests/HrDashboard.McpServer.Tests/`.
- Add `SqlServerBridgeTests.cs` alongside the existing `OracleBridgeTests.cs`, following the same structure (missing-config → unavailable message, etc.).
- Add `HrSchemaToolsTests.cs`: `ListTables`/`DescribeTable` pass-through, plus guardrail tests (SELECT-only rejection, row-cap truncation) using the fake bridge.
- Add an integration test suite that runs `SqlServerAnalyticsTools` against the real cloned `HrData` LocalDB and asserts the JSON output shape matches what `OracleAnalyticsTools` produces for the same tool call — this is the parity check that matters, since the two tool classes are hand-maintained in lockstep and could silently drift.

## Files Touched

| File | Change |
|---|---|
| `src/HrDashboard.McpServer/IOracleBridge.cs` | Renamed `IHrDataBridge.cs`; interface widened |
| `src/HrDashboard.McpServer/OracleBridge.cs` | Rewritten: ADO.NET instead of SQLcl subprocess; no longer `IHostedService` |
| `src/HrDashboard.McpServer/SqlServerBridge.cs` | New |
| `src/HrDashboard.McpServer/Tools/HrAnalyticsTools.cs` | Renamed `OracleAnalyticsTools.cs` |
| `src/HrDashboard.McpServer/Tools/SqlServerAnalyticsTools.cs` | New |
| `src/HrDashboard.McpServer/Tools/HrSchemaTools.cs` | New |
| `src/HrDashboard.McpServer/Program.cs` | Provider switch in `ConfigureServices` |
| `src/HrDashboard.McpServer/appsettings.json` | `Database:Provider` + `ConnectionStrings:HrData` replace `SqlclMcp:*` |
| `tools/HrDataClone/Program.cs` | New console tool |
| `tests/HrDashboard.McpServer.Tests/*` | Renamed fake, new bridge/tool tests, new integration suite |
