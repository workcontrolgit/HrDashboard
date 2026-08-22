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
        var canonical = _visibleTables.FirstOrDefault(t => t.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
            return Task.FromResult("[Rejected: table not in the allowed HR table list]");

        const string sql = """
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = @t0
            ORDER BY ORDINAL_POSITION
            """;
        return ExecuteAsync(sql, new Dictionary<string, object> { ["t0"] = canonical }, ct);
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
