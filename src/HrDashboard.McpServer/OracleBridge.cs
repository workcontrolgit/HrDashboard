using System.Collections.Concurrent;
using Oracle.ManagedDataAccess.Client;
using Serilog;

namespace HrDashboard.McpServer;

public sealed class OracleBridge : IHrDataBridge
{
    private readonly string _connectionString;
    private readonly string[] _visibleTables;

    // See the identical cache in SqlServerBridge for the reasoning: schema is static at
    // runtime, this class is a singleton, and an error result is never cached so a
    // transient connectivity blip gets a fresh chance to succeed on the next call.
    // NOTE: user_tables lists only base tables, not views — an Oracle view added to
    // Database:VisibleTables (e.g. to mirror SqlServerBridge's EMPLOYEES_ENRICHED /
    // DEPARTMENTS_ENRICHED) would need its own Oracle-syntax CREATE VIEW and would be
    // silently omitted from ListTablesAsync's result here even once created, since
    // user_tables doesn't include it (user_tab_columns does, so DescribeTable would still
    // work if the exact name were known) — out of scope for this pass, which only added
    // those views to the SQL Server schema.
    private string? _cachedListTables;
    private readonly ConcurrentDictionary<string, string> _describeTableCache =
        new(StringComparer.OrdinalIgnoreCase);

    public OracleBridge(IConfiguration config)
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

        var inClause = string.Join(",", _visibleTables.Select((_, i) => $":t{i}"));
        var sql = $"SELECT table_name FROM user_tables WHERE table_name IN ({inClause}) ORDER BY table_name";
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
            SELECT column_name, data_type, CASE nullable WHEN 'Y' THEN 'YES' ELSE 'NO' END AS is_nullable
            FROM user_tab_columns
            WHERE table_name = :t0
            ORDER BY column_id
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
            await using var connection = new OracleConnection(_connectionString);
            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 10;
            command.BindByName = true;
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
