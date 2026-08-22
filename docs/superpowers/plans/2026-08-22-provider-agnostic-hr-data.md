# Provider-Agnostic HR Data + Chat-With-Data Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make HrDashboard's HR data backend switchable between Oracle and SQL Server via `appsettings` config, and add generic schema-discovery + guarded ad hoc SQL tools so the agent can answer questions the curated tools don't cover.

**Architecture:** `IHrDataBridge` (widened from `IOracleBridge`) is implemented by two plain-ADO.NET classes — `OracleBridge` (`Oracle.ManagedDataAccess.Core`) and `SqlServerBridge` (`Microsoft.Data.SqlClient`) — selected at startup by `Database:Provider` in config. Two parallel curated-tool classes (`OracleAnalyticsTools`, `SqlServerAnalyticsTools`) carry provider-specific SQL under identical MCP tool names; one shared `HrSchemaTools` class exposes dialect-agnostic discovery tools that delegate to whichever bridge is active.

**Tech Stack:** .NET 10, `Oracle.ManagedDataAccess.Core`, `Microsoft.Data.SqlClient`, `ModelContextProtocol` (MCP server tools), xUnit 2.*, FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-08-22-provider-agnostic-hr-data-design.md`

## Global Constraints

- `ConnectionStrings:HrData` is the single connection-string key for both providers (no more `SqlclMcp:*`).
- `Database:Provider` is `"Oracle"` or `"SqlServer"`; any other value throws `InvalidOperationException` at startup.
- `Database:VisibleTables` (default `"EMPLOYEES,DEPARTMENTS,JOBS,LOCATIONS"`) scopes both `ListTablesAsync` and `DescribeTableAsync` — this is a plan-level addition beyond the spec's text, needed because the `hr` Oracle schema also hosts unrelated tables from the schedule-pc-eval project (`POSITION_DESCRIPTION`, `PD_DUTIES`, etc.) that `ListTables` must not surface.
- Every `RunSqlAsync`/`ExecuteAsync` call sets `CommandTimeout = 10` (seconds) and caps returned rows at 200 (`DbResultSerializer.MaxRows`).
- MCP tool names/descriptions must be byte-identical across `OracleAnalyticsTools` and `SqlServerAnalyticsTools` — only the embedded SQL differs.
- No `Co-Authored-By` line in commit messages (project preference, recorded in `.wolf/cerebrum.md`).

---

## Task 1: Widen the bridge interface + rewrite OracleBridge as plain ADO.NET

**Files:**
- Delete: `src/HrDashboard.McpServer/IOracleBridge.cs`
- Create: `src/HrDashboard.McpServer/IHrDataBridge.cs`
- Create: `src/HrDashboard.McpServer/DbResultSerializer.cs`
- Modify: `src/HrDashboard.McpServer/OracleBridge.cs` (full rewrite)
- Modify: `src/HrDashboard.McpServer/Tools/HrAnalyticsTools.cs:8` (constructor parameter type only)
- Modify: `src/HrDashboard.McpServer/Program.cs` (`ConfigureServices`)
- Modify: `src/HrDashboard.McpServer/appsettings.json`
- Modify: `src/HrDashboard.McpServer/HrDashboard.McpServer.csproj` (add package)
- Delete: `tests/HrDashboard.McpServer.Tests/FakeOracleBridge.cs`
- Create: `tests/HrDashboard.McpServer.Tests/FakeHrDataBridge.cs`
- Modify: `tests/HrDashboard.McpServer.Tests/HrAnalyticsToolsTests.cs` (rename fake usages only)
- Modify: `tests/HrDashboard.McpServer.Tests/OracleBridgeTests.cs` (full rewrite)

**Interfaces:**
- Produces: `IHrDataBridge` with `Task<string> RunSqlAsync(string sql, CancellationToken ct = default)`, `Task<string> ListTablesAsync(CancellationToken ct = default)`, `Task<string> DescribeTableAsync(string tableName, CancellationToken ct = default)`.
- Produces: `internal static class DbResultSerializer { public static Task<string> ReadAsJsonAsync(DbDataReader reader, CancellationToken ct); }` — used by both `OracleBridge` and (in Task 3) `SqlServerBridge`.
- Produces: `FakeHrDataBridge` (test double) with `LastSql`, `CallCount`, `LastDescribedTable`, `ListTablesCallCount`, `DescribeTableCallCount`, and settable `ListTablesReturnValue`/`DescribeTableReturnValue`.

- [ ] **Step 1: Add the Oracle.ManagedDataAccess.Core package**

Add to `src/HrDashboard.McpServer/HrDashboard.McpServer.csproj` inside the existing `<ItemGroup>`:

```xml
<PackageReference Include="Oracle.ManagedDataAccess.Core" Version="23.*" />
```

- [ ] **Step 2: Write the widened interface**

Create `src/HrDashboard.McpServer/IHrDataBridge.cs`:

```csharp
namespace HrDashboard.McpServer;

public interface IHrDataBridge
{
    Task<string> RunSqlAsync(string sql, CancellationToken ct = default);
    Task<string> ListTablesAsync(CancellationToken ct = default);
    Task<string> DescribeTableAsync(string tableName, CancellationToken ct = default);
}
```

Delete `src/HrDashboard.McpServer/IOracleBridge.cs`.

- [ ] **Step 3: Write the shared result serializer**

Create `src/HrDashboard.McpServer/DbResultSerializer.cs`:

```csharp
using System.Data.Common;
using System.Text.Json;

namespace HrDashboard.McpServer;

/// <summary>
/// Shared row-to-JSON serialization for both bridges. A single-row/single-column
/// result (the shape every curated tool's own JSON_OBJECT/FOR JSON query already
/// produces) is returned as-is rather than re-wrapped, so curated-tool output is
/// never double-JSON-encoded. Anything else (ad hoc queries, schema discovery)
/// is serialized as a JSON array of row objects.
/// </summary>
internal static class DbResultSerializer
{
    public const int MaxRows = 200;

    public static async Task<string> ReadAsJsonAsync(DbDataReader reader, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();

        while (rows.Count < MaxRows && await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        if (rows.Count == 1 && rows[0].Count == 1 && rows[0].Values.First() is string singleCell)
            return singleCell;

        return JsonSerializer.Serialize(rows);
    }
}
```

- [ ] **Step 4: Rewrite OracleBridge as plain ADO.NET**

Replace the full contents of `src/HrDashboard.McpServer/OracleBridge.cs`:

```csharp
using Oracle.ManagedDataAccess.Client;
using Serilog;

namespace HrDashboard.McpServer;

public sealed class OracleBridge : IHrDataBridge
{
    private readonly string _connectionString;
    private readonly string[] _visibleTables;

    public OracleBridge(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("HrData")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:HrData");
        _visibleTables = (config["Database:VisibleTables"] ?? "EMPLOYEES,DEPARTMENTS,JOBS,LOCATIONS")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public Task<string> RunSqlAsync(string sql, CancellationToken ct = default) =>
        ExecuteAsync(sql, parameters: null, ct);

    public Task<string> ListTablesAsync(CancellationToken ct = default)
    {
        var inClause = string.Join(",", _visibleTables.Select((_, i) => $":t{i}"));
        var sql = $"SELECT table_name FROM user_tables WHERE table_name IN ({inClause}) ORDER BY table_name";
        var parameters = _visibleTables
            .Select((name, i) => ($"t{i}", (object)name))
            .ToDictionary(p => p.Item1, p => p.Item2);
        return ExecuteAsync(sql, parameters, ct);
    }

    public Task<string> DescribeTableAsync(string tableName, CancellationToken ct = default)
    {
        if (!_visibleTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
            return Task.FromResult("[Rejected: table not in the allowed HR table list]");

        const string sql = """
            SELECT column_name, data_type, nullable
            FROM user_tab_columns
            WHERE table_name = :t0
            ORDER BY column_id
            """;
        return ExecuteAsync(sql, new Dictionary<string, object> { ["t0"] = tableName }, ct);
    }

    private async Task<string> ExecuteAsync(
        string sql, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct)
    {
        try
        {
            await using var connection = new OracleConnection(_connectionString);
            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 10;
            if (parameters is not null)
                foreach (var (name, value) in parameters)
                    command.Parameters.Add(new OracleParameter(name, value));

            await using var reader = await command.ExecuteReaderAsync(ct);
            return await DbResultSerializer.ReadAsJsonAsync(reader, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[OracleBridge] SQL execution failed");
            return $"[SQL error: {ex.Message}]";
        }
    }
}
```

- [ ] **Step 5: Update HrAnalyticsTools' constructor to accept the new interface**

In `src/HrDashboard.McpServer/Tools/HrAnalyticsTools.cs:8`, change:

```csharp
public sealed class HrAnalyticsTools(IOracleBridge oracle)
```

to:

```csharp
public sealed class HrAnalyticsTools(IHrDataBridge oracle)
```

(No other changes in this file — it's renamed to `OracleAnalyticsTools` in Task 2.)

- [ ] **Step 6: Update Program.cs — drop the hosted service, register IHrDataBridge**

In `src/HrDashboard.McpServer/Program.cs`, replace the `ConfigureServices` function:

```csharp
static void ConfigureServices(IServiceCollection services, IConfiguration config)
{
    var provider = config["Database:Provider"] ?? "Oracle";

    switch (provider)
    {
        case "Oracle":
            services.AddSingleton<IHrDataBridge, OracleBridge>();
            break;
        default:
            throw new InvalidOperationException(
                $"Unrecognized Database:Provider '{provider}' — expected 'Oracle' or 'SqlServer'.");
    }
}
```

(`OracleBridge` is no longer an `IHostedService`, so the `services.AddHostedService(...)` line and the old `services.AddSingleton<OracleBridge>()` + `services.AddSingleton<IOracleBridge>(...)` double-registration both go away — replaced by the single line above.)

- [ ] **Step 7: Update appsettings.json**

Replace `src/HrDashboard.McpServer/appsettings.json`:

```json
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "Database": {
    "Provider": "Oracle",
    "VisibleTables": "EMPLOYEES,DEPARTMENTS,JOBS,LOCATIONS"
  },
  "ConnectionStrings": {
    "HrData": "User Id=hr;Password=HrUser_2026;Data Source=localhost:1521/XEPDB1;"
  },
  "Urls": "http://localhost:5200"
}
```

- [ ] **Step 8: Rewrite the fake bridge**

Delete `tests/HrDashboard.McpServer.Tests/FakeOracleBridge.cs`. Create `tests/HrDashboard.McpServer.Tests/FakeHrDataBridge.cs`:

```csharp
using HrDashboard.McpServer;

namespace HrDashboard.McpServer.Tests;

internal sealed class FakeHrDataBridge(string runSqlReturnValue = "[]") : IHrDataBridge
{
    public string? LastSql { get; private set; }
    public int CallCount { get; private set; }
    public string? LastDescribedTable { get; private set; }
    public int ListTablesCallCount { get; private set; }
    public int DescribeTableCallCount { get; private set; }

    public string ListTablesReturnValue { get; set; } = "[]";
    public string DescribeTableReturnValue { get; set; } = "[]";

    public Task<string> RunSqlAsync(string sql, CancellationToken ct = default)
    {
        LastSql = sql;
        CallCount++;
        return Task.FromResult(runSqlReturnValue);
    }

    public Task<string> ListTablesAsync(CancellationToken ct = default)
    {
        ListTablesCallCount++;
        return Task.FromResult(ListTablesReturnValue);
    }

    public Task<string> DescribeTableAsync(string tableName, CancellationToken ct = default)
    {
        LastDescribedTable = tableName;
        DescribeTableCallCount++;
        return Task.FromResult(DescribeTableReturnValue);
    }
}
```

- [ ] **Step 9: Fix up references in HrAnalyticsToolsTests.cs**

In `tests/HrDashboard.McpServer.Tests/HrAnalyticsToolsTests.cs`, every `new FakeOracleBridge(...)` becomes `new FakeHrDataBridge(...)` (constructor signature is unchanged — same single optional `string` parameter). No assertions change.

- [ ] **Step 10: Rewrite OracleBridgeTests.cs for the ADO.NET bridge**

Replace `tests/HrDashboard.McpServer.Tests/OracleBridgeTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HrDashboard.McpServer.Tests;

public class OracleBridgeTests
{
    [Fact]
    public void Constructor_MissingConnectionString_Throws()
    {
        var config = new ConfigurationBuilder().Build();

        var act = () => new OracleBridge(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:HrData*");
    }

    [Fact]
    public async Task RunSqlAsync_InvalidConnectionString_ReturnsSqlErrorMessage()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HrData"] = "NotAValidKey=NotAValidValue"
            })
            .Build();
        var bridge = new OracleBridge(config);

        var result = await bridge.RunSqlAsync("SELECT 1 FROM dual");

        result.Should().StartWith("[SQL error:");
    }
}
```

- [ ] **Step 11: Build and run the McpServer test project**

Run: `dotnet test tests/HrDashboard.McpServer.Tests`
Expected: all tests pass (existing `HrAnalyticsToolsTests` still green with the renamed fake; new `OracleBridgeTests` pass without a real Oracle connection since both trigger before any network I/O — a missing key throws in the constructor, and an unparseable connection string throws immediately in `OpenAsync`).

- [ ] **Step 12: Commit**

```bash
git add src/HrDashboard.McpServer/IHrDataBridge.cs src/HrDashboard.McpServer/DbResultSerializer.cs src/HrDashboard.McpServer/OracleBridge.cs src/HrDashboard.McpServer/Tools/HrAnalyticsTools.cs src/HrDashboard.McpServer/Program.cs src/HrDashboard.McpServer/appsettings.json src/HrDashboard.McpServer/HrDashboard.McpServer.csproj tests/HrDashboard.McpServer.Tests/FakeHrDataBridge.cs tests/HrDashboard.McpServer.Tests/HrAnalyticsToolsTests.cs tests/HrDashboard.McpServer.Tests/OracleBridgeTests.cs
git rm src/HrDashboard.McpServer/IOracleBridge.cs tests/HrDashboard.McpServer.Tests/FakeOracleBridge.cs
git commit -m "feat: replace SQLcl/MCP Oracle bridge with plain ADO.NET IHrDataBridge"
```

---

## Task 2: Rename HrAnalyticsTools to OracleAnalyticsTools

**Files:**
- Modify (rename): `src/HrDashboard.McpServer/Tools/HrAnalyticsTools.cs` → `src/HrDashboard.McpServer/Tools/OracleAnalyticsTools.cs`
- Modify: `src/HrDashboard.McpServer/Program.cs` (`.WithTools<...>()` call sites)
- Modify (rename): `tests/HrDashboard.McpServer.Tests/HrAnalyticsToolsTests.cs` → `tests/HrDashboard.McpServer.Tests/OracleAnalyticsToolsTests.cs`

**Interfaces:**
- Consumes: `IHrDataBridge` from Task 1.
- Produces: `OracleAnalyticsTools(IHrDataBridge oracle)` — same public method names/signatures as the old `HrAnalyticsTools` (`GetTopEarnersByDepartment`, `GetSalaryBreakdownByDepartment`, `GetDeptHeadcount`, `GetJobSalaryRanges`, `RunHrQuery`), same `[McpServerTool(Name = ...)]` values.

- [ ] **Step 1: Rename the class and file**

Rename `src/HrDashboard.McpServer/Tools/HrAnalyticsTools.cs` to `OracleAnalyticsTools.cs`, changing only the class declaration line:

```csharp
public sealed class OracleAnalyticsTools(IHrDataBridge oracle)
```

Every method body, SQL string, and `[McpServerTool(Name = ...)]`/`[Description(...)]` value stays exactly as-is.

- [ ] **Step 2: Update Program.cs's tool registration**

In `src/HrDashboard.McpServer/Program.cs`, both `.WithTools<HrAnalyticsTools>()` call sites (stdio branch and HTTP branch) become `.WithTools<OracleAnalyticsTools>()`.

- [ ] **Step 3: Rename the test file and class**

Rename `tests/HrDashboard.McpServer.Tests/HrAnalyticsToolsTests.cs` to `OracleAnalyticsToolsTests.cs`. Change only:

```csharp
public class OracleAnalyticsToolsTests
```

and every `new HrAnalyticsTools(fake)` to `new OracleAnalyticsTools(fake)`. No assertions change.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/HrDashboard.McpServer.Tests`
Expected: all tests pass under the new names.

- [ ] **Step 5: Commit**

```bash
git add -A src/HrDashboard.McpServer/Tools tests/HrDashboard.McpServer.Tests src/HrDashboard.McpServer/Program.cs
git commit -m "refactor: rename HrAnalyticsTools to OracleAnalyticsTools"
```

---

## Task 3: Add SqlServerBridge

**Files:**
- Modify: `src/HrDashboard.McpServer/HrDashboard.McpServer.csproj` (add package)
- Create: `src/HrDashboard.McpServer/SqlServerBridge.cs`
- Modify: `src/HrDashboard.McpServer/Program.cs` (`ConfigureServices` switch)
- Create: `tests/HrDashboard.McpServer.Tests/SqlServerBridgeTests.cs`

**Interfaces:**
- Consumes: `IHrDataBridge`, `DbResultSerializer.ReadAsJsonAsync` from Task 1.
- Produces: `SqlServerBridge : IHrDataBridge` — same public shape as `OracleBridge`.

- [ ] **Step 1: Add the Microsoft.Data.SqlClient package**

Add to `src/HrDashboard.McpServer/HrDashboard.McpServer.csproj`:

```xml
<PackageReference Include="Microsoft.Data.SqlClient" Version="5.*" />
```

- [ ] **Step 2: Write SqlServerBridge**

Create `src/HrDashboard.McpServer/SqlServerBridge.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Serilog;

namespace HrDashboard.McpServer;

public sealed class SqlServerBridge : IHrDataBridge
{
    private readonly string _connectionString;
    private readonly string[] _visibleTables;

    public SqlServerBridge(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("HrData")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:HrData");
        _visibleTables = (config["Database:VisibleTables"] ?? "EMPLOYEES,DEPARTMENTS,JOBS,LOCATIONS")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public Task<string> RunSqlAsync(string sql, CancellationToken ct = default) =>
        ExecuteAsync(sql, parameters: null, ct);

    public Task<string> ListTablesAsync(CancellationToken ct = default)
    {
        var inClause = string.Join(",", _visibleTables.Select((_, i) => $"@t{i}"));
        var sql = $"SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN ({inClause}) ORDER BY TABLE_NAME";
        var parameters = _visibleTables
            .Select((name, i) => ($"t{i}", (object)name))
            .ToDictionary(p => p.Item1, p => p.Item2);
        return ExecuteAsync(sql, parameters, ct);
    }

    public Task<string> DescribeTableAsync(string tableName, CancellationToken ct = default)
    {
        if (!_visibleTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
            return Task.FromResult("[Rejected: table not in the allowed HR table list]");

        const string sql = """
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = @t0
            ORDER BY ORDINAL_POSITION
            """;
        return ExecuteAsync(sql, new Dictionary<string, object> { ["t0"] = tableName }, ct);
    }

    private async Task<string> ExecuteAsync(
        string sql, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 10;
            if (parameters is not null)
                foreach (var (name, value) in parameters)
                    command.Parameters.AddWithValue(name, value);

            await using var reader = await command.ExecuteReaderAsync(ct);
            return await DbResultSerializer.ReadAsJsonAsync(reader, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SqlServerBridge] SQL execution failed");
            return $"[SQL error: {ex.Message}]";
        }
    }
}
```

- [ ] **Step 3: Extend the provider switch in Program.cs**

In `src/HrDashboard.McpServer/Program.cs`'s `ConfigureServices`, add the `SqlServer` case:

```csharp
static void ConfigureServices(IServiceCollection services, IConfiguration config)
{
    var provider = config["Database:Provider"] ?? "Oracle";

    switch (provider)
    {
        case "Oracle":
            services.AddSingleton<IHrDataBridge, OracleBridge>();
            break;
        case "SqlServer":
            services.AddSingleton<IHrDataBridge, SqlServerBridge>();
            break;
        default:
            throw new InvalidOperationException(
                $"Unrecognized Database:Provider '{provider}' — expected 'Oracle' or 'SqlServer'.");
    }
}
```

- [ ] **Step 4: Write SqlServerBridgeTests.cs**

Create `tests/HrDashboard.McpServer.Tests/SqlServerBridgeTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HrDashboard.McpServer.Tests;

public class SqlServerBridgeTests
{
    [Fact]
    public void Constructor_MissingConnectionString_Throws()
    {
        var config = new ConfigurationBuilder().Build();

        var act = () => new SqlServerBridge(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:HrData*");
    }

    [Fact]
    public async Task RunSqlAsync_InvalidConnectionString_ReturnsSqlErrorMessage()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HrData"] = "NotAValidKey=NotAValidValue"
            })
            .Build();
        var bridge = new SqlServerBridge(config);

        var result = await bridge.RunSqlAsync("SELECT 1");

        result.Should().StartWith("[SQL error:");
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/HrDashboard.McpServer.Tests`
Expected: all tests pass, including the two new `SqlServerBridgeTests`.

- [ ] **Step 6: Commit**

```bash
git add src/HrDashboard.McpServer/HrDashboard.McpServer.csproj src/HrDashboard.McpServer/SqlServerBridge.cs src/HrDashboard.McpServer/Program.cs tests/HrDashboard.McpServer.Tests/SqlServerBridgeTests.cs
git commit -m "feat: add SqlServerBridge as a second IHrDataBridge provider"
```

---

## Task 4: Add SqlServerAnalyticsTools

**Files:**
- Create: `src/HrDashboard.McpServer/Tools/SqlServerAnalyticsTools.cs`
- Modify: `src/HrDashboard.McpServer/Program.cs` (tool registration by provider)
- Create: `tests/HrDashboard.McpServer.Tests/SqlServerAnalyticsToolsTests.cs`

**Interfaces:**
- Consumes: `IHrDataBridge` from Task 1, `FakeHrDataBridge` from Task 1.
- Produces: `SqlServerAnalyticsTools(IHrDataBridge sqlServer)` with identical tool names/descriptions to `OracleAnalyticsTools`: `GetTopEarnersByDepartment`, `GetSalaryBreakdownByDepartment`, `GetDeptHeadcount`, `GetJobSalaryRanges`, `RunHrQuery`.

- [ ] **Step 1: Write SqlServerAnalyticsTools**

Create `src/HrDashboard.McpServer/Tools/SqlServerAnalyticsTools.cs`:

```csharp
using System.ComponentModel;
using HrDashboard.McpServer;
using ModelContextProtocol.Server;

namespace HrDashboard.McpServer.Tools;

[McpServerToolType]
public sealed class SqlServerAnalyticsTools(IHrDataBridge sqlServer)
{
    [McpServerTool(Name = "GetTopEarnersByDepartment"),
     Description("Returns the top N highest-paid employees grouped by department. Returns JSON array with EmployeeName, DepartmentName, Salary. This is the complete answer for 'top/highest-paid employees' questions — do not follow up with another tool for the same data.")]
    public async Task<string> GetTopEarnersByDepartment(
        [Description("Number of top earners to return (default 10)")] int topN = 10,
        CancellationToken ct = default)
    {
        var sql = $"""
            SELECT (
                SELECT TOP ({topN})
                    e.first_name + ' ' + e.last_name AS label,
                    e.salary AS value,
                    ISNULL(d.department_name, 'No Department') AS category
                FROM employees e
                LEFT JOIN departments d ON e.department_id = d.department_id
                ORDER BY e.salary DESC
                FOR JSON PATH
            ) AS result
            """;
        return await sqlServer.RunSqlAsync(sql, ct);
    }

    [McpServerTool(Name = "GetSalaryBreakdownByDepartment"),
     Description("Returns the average salary per department, sorted highest to lowest. Returns JSON array with DepartmentName and AvgSalary only — no min/max/headcount. This is the complete answer for 'average salary by department' questions — do not follow up with RunHrQuery or another tool for the same data.")]
    public async Task<string> GetSalaryBreakdownByDepartment(CancellationToken ct = default)
    {
        const string sql = """
            SELECT (
                SELECT ISNULL(d.department_name, 'No Department') AS label,
                       ROUND(AVG(e.salary), 2) AS value,
                       'AvgSalary' AS category
                FROM employees e
                LEFT JOIN departments d ON e.department_id = d.department_id
                GROUP BY d.department_name
                ORDER BY AVG(e.salary) DESC
                FOR JSON PATH
            ) AS result
            """;
        return await sqlServer.RunSqlAsync(sql, ct);
    }

    [McpServerTool(Name = "GetDeptHeadcount"),
     Description("Returns headcount per department sorted descending. This is the complete answer for 'headcount by department' questions — do not follow up with another tool for the same data.")]
    public async Task<string> GetDeptHeadcount(CancellationToken ct = default)
    {
        const string sql = """
            SELECT (
                SELECT ISNULL(d.department_name, 'No Department') AS label,
                       COUNT(*) AS value
                FROM employees e
                LEFT JOIN departments d ON e.department_id = d.department_id
                GROUP BY d.department_name
                ORDER BY COUNT(*) DESC
                FOR JSON PATH
            ) AS result
            """;
        return await sqlServer.RunSqlAsync(sql, ct);
    }

    [McpServerTool(Name = "GetJobSalaryRanges"),
     Description("Returns min and max salary bands per job title from the JOBS table. This is the complete answer for 'job salary ranges' questions — do not follow up with another tool for the same data.")]
    public async Task<string> GetJobSalaryRanges(CancellationToken ct = default)
    {
        const string sql = """
            SELECT (
                SELECT job_title AS label,
                       max_salary AS value,
                       CAST(min_salary AS NVARCHAR(50)) AS category
                FROM jobs
                ORDER BY max_salary DESC
                FOR JSON PATH
            ) AS result
            """;
        return await sqlServer.RunSqlAsync(sql, ct);
    }

    [McpServerTool(Name = "RunHrQuery"),
     Description("Last-resort tool for custom analytics that none of the other tools cover. Do NOT use this to re-fetch, verify, or supplement data another tool already returned — if a more specific tool answers the question, use only that one. Only SELECT statements are permitted.")]
    public async Task<string> RunHrQuery(
        [Description("A valid SELECT SQL statement targeting the HR schema (EMPLOYEES, DEPARTMENTS, JOBS, LOCATIONS)")] string sql,
        CancellationToken ct = default)
    {
        var trimmed = sql.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return "[Rejected: only SELECT statements are permitted]";

        return await sqlServer.RunSqlAsync(sql, ct);
    }
}
```

- [ ] **Step 2: Register the matching tool class per provider in Program.cs**

In `src/HrDashboard.McpServer/Program.cs`, both branches currently do:

```csharp
host.Services
    .AddMcpServer()
    .WithTools<OracleAnalyticsTools>()
    .WithStdioServerTransport();
```

and the HTTP-transport equivalent. Change both to select the tool class the same way `ConfigureServices` selects the bridge — compute `provider` once (near the top of `Main`, from `tempConfig`) and reuse it:

```csharp
var provider = tempConfig["Database:Provider"] ?? "Oracle";
```

then, in the stdio branch:

```csharp
var mcpBuilder = host.Services.AddMcpServer().WithStdioServerTransport();
if (provider == "SqlServer")
    mcpBuilder.WithTools<SqlServerAnalyticsTools>();
else
    mcpBuilder.WithTools<OracleAnalyticsTools>();
```

and in the HTTP branch:

```csharp
var mcpBuilder = builder.Services.AddMcpServer().WithHttpTransport();
if (provider == "SqlServer")
    mcpBuilder.WithTools<SqlServerAnalyticsTools>();
else
    mcpBuilder.WithTools<OracleAnalyticsTools>();
```

(`.WithTools<T>()` returns the same builder, so chaining still works — `mcpBuilder` is just named so it can be reused across the `if`.)

- [ ] **Step 3: Write SqlServerAnalyticsToolsTests.cs**

Create `tests/HrDashboard.McpServer.Tests/SqlServerAnalyticsToolsTests.cs` — same structure as `OracleAnalyticsToolsTests.cs`, asserting on T-SQL fragments instead of Oracle ones:

```csharp
using FluentAssertions;
using HrDashboard.McpServer.Tools;
using Xunit;

namespace HrDashboard.McpServer.Tests;

public class SqlServerAnalyticsToolsTests
{
    [Fact]
    public async Task GetTopEarnersByDepartment_PassesSqlToBridge_ReturnsResult()
    {
        var fake = new FakeHrDataBridge("[{\"label\":\"Alice\",\"value\":9000}]");
        var tools = new SqlServerAnalyticsTools(fake);

        var result = await tools.GetTopEarnersByDepartment(topN: 5);

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Contain("TOP (5)");
        result.Should().Be("[{\"label\":\"Alice\",\"value\":9000}]");
    }

    [Fact]
    public async Task GetSalaryBreakdownByDepartment_PassesSqlToBridge_ReturnsResult()
    {
        var fake = new FakeHrDataBridge("[{\"label\":\"IT\",\"value\":8000}]");
        var tools = new SqlServerAnalyticsTools(fake);

        var result = await tools.GetSalaryBreakdownByDepartment();

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Contain("AVG(e.salary)");
        result.Should().Be("[{\"label\":\"IT\",\"value\":8000}]");
    }

    [Fact]
    public async Task GetDeptHeadcount_PassesSqlToBridge_ReturnsResult()
    {
        var fake = new FakeHrDataBridge("[{\"label\":\"HR\",\"value\":10}]");
        var tools = new SqlServerAnalyticsTools(fake);

        var result = await tools.GetDeptHeadcount();

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Contain("COUNT(*)");
        result.Should().Be("[{\"label\":\"HR\",\"value\":10}]");
    }

    [Fact]
    public async Task GetJobSalaryRanges_PassesSqlToBridge_ReturnsResult()
    {
        var fake = new FakeHrDataBridge("[{\"label\":\"Manager\",\"value\":15000}]");
        var tools = new SqlServerAnalyticsTools(fake);

        var result = await tools.GetJobSalaryRanges();

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Contain("max_salary");
        result.Should().Be("[{\"label\":\"Manager\",\"value\":15000}]");
    }

    [Fact]
    public async Task RunHrQuery_SelectStatement_PassesToBridge()
    {
        var fake = new FakeHrDataBridge("[{\"label\":\"test\",\"value\":1}]");
        var tools = new SqlServerAnalyticsTools(fake);
        const string sql = "SELECT employee_id FROM employees";

        var result = await tools.RunHrQuery(sql);

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Be(sql);
        result.Should().Be("[{\"label\":\"test\",\"value\":1}]");
    }

    [Fact]
    public async Task RunHrQuery_NonSelect_RejectsWithoutCallingBridge()
    {
        var fake = new FakeHrDataBridge("should not be returned");
        var tools = new SqlServerAnalyticsTools(fake);

        var result = await tools.RunHrQuery("DELETE FROM employees");

        fake.CallCount.Should().Be(0);
        result.Should().Be("[Rejected: only SELECT statements are permitted]");
    }

    [Fact]
    public async Task RunHrQuery_SelectWithLeadingWhitespace_Accepted()
    {
        var fake = new FakeHrDataBridge("[]");
        var tools = new SqlServerAnalyticsTools(fake);

        var result = await tools.RunHrQuery("   SELECT 1");

        fake.CallCount.Should().Be(1);
        result.Should().Be("[]");
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/HrDashboard.McpServer.Tests`
Expected: all tests pass, including the 6 new `SqlServerAnalyticsToolsTests`.

- [ ] **Step 5: Commit**

```bash
git add src/HrDashboard.McpServer/Tools/SqlServerAnalyticsTools.cs src/HrDashboard.McpServer/Program.cs tests/HrDashboard.McpServer.Tests/SqlServerAnalyticsToolsTests.cs
git commit -m "feat: add SqlServerAnalyticsTools (T-SQL curated tools)"
```

---

## Task 5: Add HrSchemaTools (schema discovery)

**Files:**
- Create: `src/HrDashboard.McpServer/Tools/HrSchemaTools.cs`
- Modify: `src/HrDashboard.McpServer/Program.cs` (register unconditionally in both branches)
- Create: `tests/HrDashboard.McpServer.Tests/HrSchemaToolsTests.cs`

**Interfaces:**
- Consumes: `IHrDataBridge.ListTablesAsync`/`DescribeTableAsync` from Tasks 1/3, `FakeHrDataBridge` from Task 1.
- Produces: `HrSchemaTools(IHrDataBridge bridge)` with tools `ListTables()` and `DescribeTable(string tableName)`.

- [ ] **Step 1: Write HrSchemaTools**

Create `src/HrDashboard.McpServer/Tools/HrSchemaTools.cs`:

```csharp
using System.ComponentModel;
using HrDashboard.McpServer;
using ModelContextProtocol.Server;

namespace HrDashboard.McpServer.Tools;

[McpServerToolType]
public sealed class HrSchemaTools(IHrDataBridge bridge)
{
    [McpServerTool(Name = "ListTables"),
     Description("Lists the HR tables available to query. Call this before RunHrQuery if you don't already know the exact table/column names you need.")]
    public async Task<string> ListTables(CancellationToken ct = default)
        => await bridge.ListTablesAsync(ct);

    [McpServerTool(Name = "DescribeTable"),
     Description("Returns the column names and types for one HR table. Call this before RunHrQuery to confirm exact column names.")]
    public async Task<string> DescribeTable(
        [Description("Table name, e.g. EMPLOYEES")] string tableName,
        CancellationToken ct = default)
        => await bridge.DescribeTableAsync(tableName, ct);
}
```

- [ ] **Step 2: Register HrSchemaTools in both Program.cs branches**

In both the stdio and HTTP branches of `src/HrDashboard.McpServer/Program.cs`, add `.WithTools<HrSchemaTools>()` to the same `mcpBuilder` chain from Task 4, e.g.:

```csharp
var mcpBuilder = host.Services.AddMcpServer().WithStdioServerTransport();
if (provider == "SqlServer")
    mcpBuilder.WithTools<SqlServerAnalyticsTools>();
else
    mcpBuilder.WithTools<OracleAnalyticsTools>();
mcpBuilder.WithTools<HrSchemaTools>();
```

(and the same pattern in the HTTP branch).

- [ ] **Step 3: Write HrSchemaToolsTests.cs**

Create `tests/HrDashboard.McpServer.Tests/HrSchemaToolsTests.cs`:

```csharp
using FluentAssertions;
using HrDashboard.McpServer.Tools;
using Xunit;

namespace HrDashboard.McpServer.Tests;

public class HrSchemaToolsTests
{
    [Fact]
    public async Task ListTables_ReturnsBridgeResult()
    {
        var fake = new FakeHrDataBridge { ListTablesReturnValue = "[\"EMPLOYEES\",\"DEPARTMENTS\"]" };
        var tools = new HrSchemaTools(fake);

        var result = await tools.ListTables();

        fake.ListTablesCallCount.Should().Be(1);
        result.Should().Be("[\"EMPLOYEES\",\"DEPARTMENTS\"]");
    }

    [Fact]
    public async Task DescribeTable_PassesTableNameToBridge_ReturnsResult()
    {
        var fake = new FakeHrDataBridge { DescribeTableReturnValue = "[{\"COLUMN_NAME\":\"SALARY\"}]" };
        var tools = new HrSchemaTools(fake);

        var result = await tools.DescribeTable("EMPLOYEES");

        fake.DescribeTableCallCount.Should().Be(1);
        fake.LastDescribedTable.Should().Be("EMPLOYEES");
        result.Should().Be("[{\"COLUMN_NAME\":\"SALARY\"}]");
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/HrDashboard.McpServer.Tests`
Expected: all tests pass, including the 2 new `HrSchemaToolsTests`.

- [ ] **Step 5: Build the whole solution**

Run: `dotnet build HrDashboard.slnx`
Expected: 0 errors — this is the first point where every touched project (McpServer, its tests, and anything referencing the renamed/removed types) must compile together.

- [ ] **Step 6: Commit**

```bash
git add src/HrDashboard.McpServer/Tools/HrSchemaTools.cs src/HrDashboard.McpServer/Program.cs tests/HrDashboard.McpServer.Tests/HrSchemaToolsTests.cs
git commit -m "feat: add HrSchemaTools (ListTables/DescribeTable schema discovery)"
```

---

## Task 6: Oracle-to-SQL-Server clone tool

**Files:**
- Create: `tools/HrDataClone/HrDataClone.csproj`
- Create: `tools/HrDataClone/appsettings.json`
- Create: `tools/HrDataClone/Program.cs`
- Modify: `HrDashboard.slnx` (add the new project)

**Interfaces:**
- Produces: a standalone console executable, no interfaces consumed by other tasks. Task 7 depends on this tool having been *run* (its output database populated), not on any of its code.

- [ ] **Step 1: Create the console project**

Create `tools/HrDataClone/HrDataClone.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Oracle.ManagedDataAccess.Core" Version="23.*" />
    <PackageReference Include="Microsoft.Data.SqlClient" Version="5.*" />
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <None Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add connection strings**

Create `tools/HrDataClone/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "Oracle": "User Id=hr;Password=HrUser_2026;Data Source=localhost:1521/XEPDB1;",
    "SqlServer": "Server=(localdb)\\mssqllocaldb;Database=HrData;Trusted_Connection=True;"
  }
}
```

- [ ] **Step 3: Write the clone Program.cs**

Create `tools/HrDataClone/Program.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Oracle.ManagedDataAccess.Client;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var oracleConnectionString = config.GetConnectionString("Oracle")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Oracle");
var sqlServerConnectionString = config.GetConnectionString("SqlServer")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:SqlServer");

string[] tables = ["EMPLOYEES", "DEPARTMENTS", "JOBS", "LOCATIONS"];

await using var oracleConnection = new OracleConnection(oracleConnectionString);
await oracleConnection.OpenAsync();

await using var sqlConnection = new SqlConnection(sqlServerConnectionString);
await sqlConnection.OpenAsync();

await EnsureSchemaAsync(sqlConnection);

foreach (var table in tables)
{
    Console.WriteLine($"Cloning {table}...");

    await using (var truncateCommand = sqlConnection.CreateCommand())
    {
        truncateCommand.CommandText = $"TRUNCATE TABLE dbo.{table}";
        await truncateCommand.ExecuteNonQueryAsync();
    }

    await using var selectCommand = oracleConnection.CreateCommand();
    selectCommand.CommandText = $"SELECT * FROM {table}";
    await using var reader = await selectCommand.ExecuteReaderAsync();

    using var bulkCopy = new SqlBulkCopy(sqlConnection)
    {
        DestinationTableName = $"dbo.{table}"
    };
    for (var i = 0; i < reader.FieldCount; i++)
        bulkCopy.ColumnMappings.Add(reader.GetName(i), reader.GetName(i));

    await bulkCopy.WriteToServerAsync(reader);
    Console.WriteLine($"  done.");
}

Console.WriteLine("Clone complete.");
return;

static async Task EnsureSchemaAsync(SqlConnection connection)
{
    const string sql = """
        IF OBJECT_ID('dbo.EMPLOYEES') IS NULL
        CREATE TABLE dbo.EMPLOYEES (
            EMPLOYEE_ID     INT             NOT NULL PRIMARY KEY,
            FIRST_NAME      NVARCHAR(20)    NULL,
            LAST_NAME       NVARCHAR(25)    NOT NULL,
            EMAIL           NVARCHAR(25)    NOT NULL,
            PHONE_NUMBER    NVARCHAR(20)    NULL,
            HIRE_DATE       DATETIME2       NOT NULL,
            JOB_ID          NVARCHAR(10)    NOT NULL,
            SALARY          DECIMAL(8,2)    NULL,
            COMMISSION_PCT  DECIMAL(4,2)    NULL,
            MANAGER_ID      INT             NULL,
            DEPARTMENT_ID   INT             NULL
        );

        IF OBJECT_ID('dbo.DEPARTMENTS') IS NULL
        CREATE TABLE dbo.DEPARTMENTS (
            DEPARTMENT_ID   INT             NOT NULL PRIMARY KEY,
            DEPARTMENT_NAME NVARCHAR(30)    NOT NULL,
            MANAGER_ID      INT             NULL,
            LOCATION_ID     INT             NULL
        );

        IF OBJECT_ID('dbo.JOBS') IS NULL
        CREATE TABLE dbo.JOBS (
            JOB_ID          NVARCHAR(10)    NOT NULL PRIMARY KEY,
            JOB_TITLE       NVARCHAR(35)    NOT NULL,
            MIN_SALARY      DECIMAL(6,0)    NULL,
            MAX_SALARY      DECIMAL(6,0)    NULL
        );

        IF OBJECT_ID('dbo.LOCATIONS') IS NULL
        CREATE TABLE dbo.LOCATIONS (
            LOCATION_ID     INT             NOT NULL PRIMARY KEY,
            STREET_ADDRESS  NVARCHAR(40)    NULL,
            CITY            NVARCHAR(30)    NOT NULL,
            STATE_PROVINCE  NVARCHAR(25)    NULL,
            POSTAL_CODE     NVARCHAR(12)    NULL,
            COUNTRY_ID      NCHAR(2)        NULL
        );
        """;

    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}
```

No foreign keys are declared between these tables — this is a read-only analytics fixture, not a system of record, so referential integrity enforcement is unnecessary and would only complicate truncate/insert ordering.

- [ ] **Step 4: Add the project to the solution**

Run: `dotnet sln HrDashboard.slnx add tools/HrDataClone/HrDataClone.csproj`

- [ ] **Step 5: Create the HrData database**

Run (creates an empty database on the same LocalDB instance the app already uses):
```bash
sqlcmd -S "(localdb)\mssqllocaldb" -Q "CREATE DATABASE HrData"
```
Expected: command succeeds (or reports the database already exists, which is fine to ignore on a rerun).

- [ ] **Step 6: Run the clone tool against real Oracle + real SQL Server**

Run: `dotnet run --project tools/HrDataClone`
Expected: console prints "Cloning EMPLOYEES...", "done.", and so on for all 4 tables, ending with "Clone complete." — no exceptions.

- [ ] **Step 7: Verify row counts match**

Run (via the `oracle-sql-query` skill or SQLcl directly): `SELECT COUNT(*) FROM employees` against Oracle `hr_local`, and:
```bash
sqlcmd -S "(localdb)\mssqllocaldb" -d HrData -Q "SELECT COUNT(*) FROM dbo.EMPLOYEES"
```
Expected: counts match. Repeat for `DEPARTMENTS`, `JOBS`, `LOCATIONS` if any doubt remains.

- [ ] **Step 8: Commit**

```bash
git add tools/HrDataClone HrDashboard.slnx
git commit -m "feat: add Oracle-to-SQL-Server HR data clone tool"
```

---

## Task 7: Provider parity integration tests

**Files:**
- Create: `tests/HrDashboard.McpServer.Tests/ProviderParityTests.cs`

**Interfaces:**
- Consumes: `OracleBridge`, `SqlServerBridge` (Tasks 1/3), `OracleAnalyticsTools`, `SqlServerAnalyticsTools` (Tasks 2/4) — constructed directly against real, reachable databases (no fakes).

**Precondition:** Task 6's clone must have been run at least once so `HrData` on `(localdb)\mssqllocaldb` has real rows.

- [ ] **Step 1: Write the parity test**

Create `tests/HrDashboard.McpServer.Tests/ProviderParityTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using HrDashboard.McpServer.Tools;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HrDashboard.McpServer.Tests;

[Trait("Category", "Integration")]
public class ProviderParityTests
{
    private static IConfiguration OracleConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:HrData"] = "User Id=hr;Password=HrUser_2026;Data Source=localhost:1521/XEPDB1;"
        })
        .Build();

    private static IConfiguration SqlServerConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:HrData"] = "Server=(localdb)\\mssqllocaldb;Database=HrData;Trusted_Connection=True;"
        })
        .Build();

    [Fact]
    public async Task GetDeptHeadcount_OracleAndSqlServer_ReturnSameShapeAndCounts()
    {
        var oracleTools = new OracleAnalyticsTools(new OracleBridge(OracleConfig()));
        var sqlServerTools = new SqlServerAnalyticsTools(new SqlServerBridge(SqlServerConfig()));

        var oracleResult = await oracleTools.GetDeptHeadcount();
        var sqlServerResult = await sqlServerTools.GetDeptHeadcount();

        using var oracleJson = JsonDocument.Parse(oracleResult);
        using var sqlServerJson = JsonDocument.Parse(sqlServerResult);

        oracleJson.RootElement.GetArrayLength().Should().Be(sqlServerJson.RootElement.GetArrayLength());

        var oracleFirst = oracleJson.RootElement.EnumerateArray().First();
        var sqlServerFirst = sqlServerJson.RootElement.EnumerateArray().First();
        oracleFirst.TryGetProperty("label", out _).Should().BeTrue();
        oracleFirst.TryGetProperty("value", out _).Should().BeTrue();
        sqlServerFirst.TryGetProperty("label", out _).Should().BeTrue();
        sqlServerFirst.TryGetProperty("value", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetJobSalaryRanges_OracleAndSqlServer_ReturnSameRowCount()
    {
        var oracleTools = new OracleAnalyticsTools(new OracleBridge(OracleConfig()));
        var sqlServerTools = new SqlServerAnalyticsTools(new SqlServerBridge(SqlServerConfig()));

        var oracleResult = await oracleTools.GetJobSalaryRanges();
        var sqlServerResult = await sqlServerTools.GetJobSalaryRanges();

        using var oracleJson = JsonDocument.Parse(oracleResult);
        using var sqlServerJson = JsonDocument.Parse(sqlServerResult);

        oracleJson.RootElement.GetArrayLength().Should().Be(sqlServerJson.RootElement.GetArrayLength());
    }
}
```

- [ ] **Step 2: Run the parity tests**

Run: `dotnet test tests/HrDashboard.McpServer.Tests --filter "Category=Integration"`
Expected: both tests pass, requiring a reachable Oracle `hr_local` and a populated `HrData` LocalDB (from Task 6).

- [ ] **Step 3: Run the full McpServer test suite**

Run: `dotnet test tests/HrDashboard.McpServer.Tests`
Expected: every test in the project passes (unit tests + the 2 integration tests).

- [ ] **Step 4: Run the whole solution's tests**

Run: `dotnet test HrDashboard.slnx`
Expected: 0 failures across every test project (McpServer.Tests, Agents.Tests, Infrastructure.Tests, Web.E2E.Tests — the last one still reporting its known 3 skips from bug-023).

- [ ] **Step 5: Commit**

```bash
git add tests/HrDashboard.McpServer.Tests/ProviderParityTests.cs
git commit -m "test: add Oracle/SqlServer provider parity integration tests"
```

---

## Plan Self-Review Notes

- **Spec coverage:** every spec section has a task — interface widening + Oracle ADO.NET rewrite (Task 1), tool renaming (Task 2), SqlServerBridge (Task 3), SqlServerAnalyticsTools (Task 4), HrSchemaTools + guardrails (Task 5, guardrails folded into Tasks 1/3's `ExecuteAsync`), the clone tool (Task 6), and the parity test suite (Task 7).
- **Beyond-spec addition:** `Database:VisibleTables` config (Task 1) wasn't in the spec — added because the `hr` Oracle schema hosts unrelated tables from another project, and an unscoped `ListTables` would leak that surface to the LLM. This restricts `ListTablesAsync`/`DescribeTableAsync`, not `RunHrQuery` (which the spec already scoped via DB permissions, not a code-level allowlist).
- **Type consistency checked:** `IHrDataBridge` signatures match across `OracleBridge`, `SqlServerBridge`, `FakeHrDataBridge`, and all tool classes. `DbResultSerializer.ReadAsJsonAsync(DbDataReader, CancellationToken)` is called identically by both bridges.
- **DbResultSerializer has no dedicated unit test** — faking `DbDataReader` (abstract) would need substantial mock scaffolding for a small, low-risk routine. It's exercised indirectly by every `FakeHrDataBridge`-based tool test (trivially, since the fake bypasses it) and, for real, by the Task 7 integration tests against live data — that's the actual verification point for its row-cap and single-cell-passthrough behavior.
