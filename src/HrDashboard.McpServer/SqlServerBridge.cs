using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Serilog;

namespace HrDashboard.McpServer;

public sealed class SqlServerBridge : IHrDataBridge
{
    private readonly string _connectionString;
    private readonly string[] _visibleTables;

    // Schema (table/view list and column layout) doesn't change while the process is
    // running, and this class is registered as a singleton — so caching indefinitely is
    // safe and turns every ListTables/DescribeTable call after the first into a plain
    // in-memory lookup instead of a fresh INFORMATION_SCHEMA round trip. An error result
    // (e.g. a transient connectivity blip) is deliberately never cached, so the next call
    // gets a fresh chance to succeed once the DB is reachable again.
    private string? _cachedListTables;
    private readonly ConcurrentDictionary<string, string> _describeTableCache =
        new(StringComparer.OrdinalIgnoreCase);

    public SqlServerBridge(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("HrData")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:HrData");
        _visibleTables = (config["Database:VisibleTables"] ?? "EMPLOYEES,DEPARTMENTS,JOBS,LOCATIONS")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public Task<string> RunSqlAsync(string sql, CancellationToken ct = default) =>
        ExecuteAsync(sql, parameters: null, ct);

    public async Task<string> ListTablesAsync(CancellationToken ct = default)
    {
        if (_cachedListTables is not null) return _cachedListTables;

        var inClause = string.Join(",", _visibleTables.Select((_, i) => $"@t{i}"));
        var sql = $"SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN ({inClause}) ORDER BY TABLE_NAME";
        var parameters = _visibleTables
            .Select((name, i) => ($"t{i}", (object)name))
            .ToDictionary(p => p.Item1, p => p.Item2);
        var result = await ExecuteAsync(sql, parameters, ct);
        if (!IsSqlError(result))
            _cachedListTables = result;
        return result;
    }

    public async Task<string> DescribeTableAsync(string tableName, CancellationToken ct = default)
    {
        var canonical = _visibleTables.FirstOrDefault(t => t.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
            return "[Rejected: table not in the allowed HR table list]";

        if (_describeTableCache.TryGetValue(canonical, out var cached))
            return cached;

        const string sql = """
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = @t0
            ORDER BY ORDINAL_POSITION
            """;
        var result = await ExecuteAsync(sql, new Dictionary<string, object> { ["t0"] = canonical }, ct);
        if (!IsSqlError(result))
            _describeTableCache[canonical] = result;
        return result;
    }

    private static bool IsSqlError(string result) =>
        result.StartsWith("[SQL error", StringComparison.Ordinal);

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
