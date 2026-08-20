namespace HrDashboard.McpServer;

public interface IOracleBridge
{
    Task<string> RunSqlAsync(string sql, CancellationToken ct = default);
}
